// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.
// See license.txt file in the project root for full license information.

using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json;
using ByteSizeLib;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Parsers.Clr;
using Microsoft.Diagnostics.Tracing.Session;

namespace Ultra.Core;

public class EtwUltraProfiler : IDisposable
{
    private TraceEventSession? _userSession;
    private TraceEventSession? _kernelSession;
    private bool _cancelRequested;
    private ManualResetEvent? _cleanCancel;
    private bool _stopRequested;

    public bool Cancel()
    {
        if (!_cancelRequested)
        {
            _cleanCancel = new ManualResetEvent(false);
            _cancelRequested = true;
            return false;
        }
        else
        {
            _stopRequested = true;

            // Before really canceling, wait for the clean cancel to be done
            WaitForCleanCancel();
            return true;
        }
    }

    private void WaitForCleanCancel()
    {
        if (_cleanCancel is not null)
        {
            _cleanCancel.WaitOne();
            _cleanCancel.Dispose();
            _cleanCancel = null;
        }
    }

    public static bool IsElevated()
    {
        var isElevated = TraceEventSession.IsElevated();
        return isElevated.HasValue && isElevated.Value;
    }

    public async Task<string> Run(EtwUltraProfilerOptions ultraProfilerOptions)
    {
        List<System.Diagnostics.Process> processList = new List<System.Diagnostics.Process>();
        if (ultraProfilerOptions.ProcessIds.Count > 0)
        {
            foreach (var pidToAttach in ultraProfilerOptions.ProcessIds)
            {
                try
                {
                    var process = System.Diagnostics.Process.GetProcessById(pidToAttach);
                    processList.Add(process);
                }
                catch (ArgumentException ex)
                {
                    throw new ArgumentException($"Unable to find Process with pid {pidToAttach}");
                }
            }
        }
        
        if (processList.Count == 0 && ultraProfilerOptions.ProgramPath is null)
        {
            throw new ArgumentException("pid is required or an executable with optional arguments");
        }

        string? processName = null;

        System.Diagnostics.Process? singleProcess = null;
        
        if (processList.Count == 1 && ultraProfilerOptions.ProgramPath is null)
        {
            singleProcess = processList[0];
            processName = singleProcess.ProcessName;
        }
        else if (ultraProfilerOptions.ProgramPath != null)
        {
            if (!ultraProfilerOptions.ProgramPath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException($"Executable path {ultraProfilerOptions.ProgramPath} must end with .exe");
            }

            processName = Path.GetFileNameWithoutExtension(ultraProfilerOptions.ProgramPath);
        }

        var currentTime = DateTime.Now;
        var baseName = processName != null ? $"ultra_{processName}_{currentTime:yyyy-MM-dd_HH_mm_ss}" : $"ultra_{currentTime:yyyy-MM-dd_HH_mm_ss}";

        // Append the pid for a single process that we are attaching to
        if (singleProcess is not null)
        {
            baseName = $"{baseName}_{singleProcess.Id}";
        }
        
        var options = new TraceEventProviderOptions()
        {
            StacksEnabled = true,
        };
        
        // Filter the requested process ids
        if (processList.Count > 0)
        {
            options.ProcessIDFilter = new List<int>();
            foreach (var process in processList)
            {
                options.ProcessIDFilter.Add(process.Id);
            }
        }

        // Make sure to filter the process name if we have a single process
        if (ultraProfilerOptions.ProgramPath != null)
        {
            options.ProcessNameFilter = [Path.GetFileName(ultraProfilerOptions.ProgramPath)];
        }
        
        var kernelFileName = $"{baseName}.kernel.etl";
        var userFileName = $"{baseName}.user.etl";

        var clock = Stopwatch.StartNew();
        var lastTime = clock.Elapsed;

        _userSession = new TraceEventSession($"{baseName}-user", userFileName);
        _kernelSession = new TraceEventSession($"{baseName}-kernel", kernelFileName);

        try
        {
            using (_userSession)
            using (_kernelSession)
            {
                _kernelSession.StopOnDispose = true;
                _kernelSession.CircularBufferMB = 0;
                _kernelSession.CpuSampleIntervalMSec = ultraProfilerOptions.CpuSamplingIntervalInMs;
                _kernelSession.StackCompression = false;

                _userSession.StopOnDispose = true;
                _userSession.CircularBufferMB = 0;
                _userSession.CpuSampleIntervalMSec = ultraProfilerOptions.CpuSamplingIntervalInMs;
                _userSession.StackCompression = false;

                var kernelEvents = KernelTraceEventParser.Keywords.Profile
                                   | KernelTraceEventParser.Keywords.ContextSwitch
                                   | KernelTraceEventParser.Keywords.ImageLoad
                                   | KernelTraceEventParser.Keywords.Process
                                   | KernelTraceEventParser.Keywords.Thread;
                _kernelSession.EnableKernelProvider(kernelEvents, KernelTraceEventParser.Keywords.Profile);

                var jitEvents = ClrTraceEventParser.Keywords.JITSymbols |
                                ClrTraceEventParser.Keywords.Exception |
                                ClrTraceEventParser.Keywords.GC |
                                ClrTraceEventParser.Keywords.GCHeapAndTypeNames |
                                ClrTraceEventParser.Keywords.Interop |
                                ClrTraceEventParser.Keywords.JITSymbols |
                                ClrTraceEventParser.Keywords.Jit |
                                ClrTraceEventParser.Keywords.JittedMethodILToNativeMap |
                                ClrTraceEventParser.Keywords.Loader |
                                ClrTraceEventParser.Keywords.Stack |
                                ClrTraceEventParser.Keywords.StartEnumeration;

                _userSession.EnableProvider(
                    ClrTraceEventParser.ProviderGuid,
                    TraceEventLevel.Verbose, // For call stacks.
                    (ulong)jitEvents, options);

                HashSet<Process> exitedProcessList = new();

                // Start a command line process if needed
                if (ultraProfilerOptions.ProgramPath is not null)
                {
                    var startInfo = new ProcessStartInfo
                    {
                        FileName = ultraProfilerOptions.ProgramPath,
                        UseShellExecute = true,
                        CreateNoWindow = true,
                        WindowStyle = ProcessWindowStyle.Hidden
                    };

                    foreach (var arg in ultraProfilerOptions.Arguments)
                    {
                        startInfo.ArgumentList.Add(arg);
                    }

                    ultraProfilerOptions.LogProgress?.Invoke($"Starting Process {startInfo.FileName} {string.Join(" ", startInfo.ArgumentList)}");
                    var process = System.Diagnostics.Process.Start(startInfo)!;
                    processList.Add(process);
                    singleProcess ??= process;
                }

                foreach (var process in processList)
                {
                    ultraProfilerOptions.LogProgress?.Invoke($"Start Profiling Process {process.ProcessName} ({process.Id})");
                }

                // Collect the data until all processes have exited or there is a cancel request
                while (!_cancelRequested)
                {
                    if (clock.Elapsed.TotalMilliseconds - lastTime.TotalMilliseconds > ultraProfilerOptions.UpdateLogAfterInMs)
                    {
                        var userFileNameLength = new FileInfo(userFileName).Length;
                        var kernelFileNameLength = new FileInfo(kernelFileName).Length;
                        var totalFileNameLength = userFileNameLength + kernelFileNameLength;

                        ultraProfilerOptions.LogStepProgress?.Invoke(singleProcess is not null
                            ? $"Profiling Process {singleProcess.ProcessName} ({singleProcess.Id}) - {(int)clock.Elapsed.TotalSeconds}s - {ByteSize.FromBytes(totalFileNameLength)}"
                            : $"Profiling {processList.Count} Processes - {(int)clock.Elapsed.TotalSeconds}s - {ByteSize.FromBytes(totalFileNameLength)}");
                        lastTime = clock.Elapsed;
                    }

                    await Task.Delay(ultraProfilerOptions.CheckDeltaTimeInMs);

                    foreach (var process in processList)
                    {
                        if (process.HasExited && exitedProcessList.Add(process))
                        {
                            ultraProfilerOptions.LogProgress?.Invoke($"Process {process.ProcessName} ({process.Id}) has exited");
                        }
                    }

                    if (exitedProcessList.Count == processList.Count)
                    {
                        break;
                    }

                } // Needed for JIT Compile code that was already compiled. 

                _kernelSession.Stop();
                _userSession.Stop();

                ultraProfilerOptions.LogProgress?.Invoke(singleProcess is not null ? $"End Profiling Process" : $"End Profiling {processList.Count} Processes");

                await WaitForStaleFile(userFileName, ultraProfilerOptions);
                await WaitForStaleFile(kernelFileName, ultraProfilerOptions);
            }
        }
        catch
        {
            // Delete intermediate files if we have an exception
            File.Delete(kernelFileName);
            File.Delete(userFileName);
            throw;
        }
        finally
        {
            _userSession = null;
            _kernelSession = null;
            _cleanCancel?.Set();
        }

        if (_stopRequested)
        {
            throw new InvalidOperationException("CTRL+C requested");
        }

        var rundownSession = $"{baseName}.rundown.etl";
        using (TraceEventSession clrRundownSession = new TraceEventSession($"{baseName}-rundown", rundownSession))
        {
            clrRundownSession.StopOnDispose = true;
            clrRundownSession.CircularBufferMB = 0;

            ultraProfilerOptions.LogProgress?.Invoke($"Running CLR Rundown");

            // The runtime does method rundown first then the module rundown.  This means if you have a large
            // number of methods and method rundown does not complete you don't get ANYTHING.   To avoid this
            // we first trigger all module (loader) rundown and then trigger the method rundown
            clrRundownSession.EnableProvider(
                ClrRundownTraceEventParser.ProviderGuid,
                TraceEventLevel.Verbose,
                (ulong)(ClrRundownTraceEventParser.Keywords.Loader | ClrRundownTraceEventParser.Keywords.ForceEndRundown), options);

            await Task.Delay(500);

            clrRundownSession.EnableProvider(
                ClrRundownTraceEventParser.ProviderGuid,
                TraceEventLevel.Verbose,
                (ulong)(ClrRundownTraceEventParser.Keywords.Default & ~ClrRundownTraceEventParser.Keywords.Loader), options);
            
            await Task.Delay(500);

            await WaitForStaleFile(rundownSession, ultraProfilerOptions);
        }

        if (_stopRequested)
        {
            throw new InvalidOperationException("CTRL+C requested");
        }

        ultraProfilerOptions.LogProgress?.Invoke($"Merging ETL Files");
        // Merge file (and to force Volume mapping)
        var etlFinalFile = $"{baseName}.etl";
        TraceEventSession.Merge([kernelFileName, userFileName, rundownSession], etlFinalFile);
        //TraceEventSession.Merge([kernelFileName, userFileName], $"{baseName}.etl");

        if (_stopRequested)
        {
            throw new InvalidOperationException("CTRL+C requested");
        }

        if (!ultraProfilerOptions.KeepEtlIntermediateFiles)
        {
            File.Delete(kernelFileName);
            File.Delete(userFileName);
            File.Delete(rundownSession);
        }

        if (_stopRequested)
        {
            throw new InvalidOperationException("CTRL+C requested");
        }

        var jsonFinalFile = await Convert(etlFinalFile, processList.Select(x => x.Id).ToList(), ultraProfilerOptions);

        if (!ultraProfilerOptions.KeepMergedEtl)
        {
            File.Delete(etlFinalFile);
            File.Delete($"{baseName}.etlx");
        }
        
        return jsonFinalFile;
    }

    public async Task<string> Convert(string etlFile, List<int> pIds, EtwUltraProfilerOptions ultraProfilerOptions)
    {
        var etlProcessor = new EtwConverterToFirefox();
        var profile = etlProcessor.Convert(etlFile, pIds, ultraProfilerOptions);

        if (_stopRequested)
        {
            throw new InvalidOperationException("CTRL+C requested");
        }

        var directory = Path.GetDirectoryName(etlFile);
        var etlFileNameWithoutExtension = Path.GetFileNameWithoutExtension(etlFile);
        var jsonFinalFile = $"{etlFileNameWithoutExtension}.json.gz";
        ultraProfilerOptions.LogProgress?.Invoke($"Converting to Firefox Profiler JSON");
        await using var stream = File.Create(jsonFinalFile);
        await using var gzipStream = new GZipStream(stream, CompressionLevel.Optimal);
        await JsonSerializer.SerializeAsync(gzipStream, profile, FirefoxProfiler.JsonProfilerContext.Default.Profile);
        gzipStream.Flush();

        return jsonFinalFile;
    }

    private async Task WaitForStaleFile(string file, EtwUltraProfilerOptions options)
    {
        var clock = Stopwatch.StartNew();
        var startTime = clock.ElapsedMilliseconds;
        var fileInfo = new FileInfo(file);
        if (!fileInfo.Exists) return;
        var length = fileInfo.Length;
        long lastTimeLogInMs = -1;
        while (true)
        {
            fileInfo.Refresh();
            var newLength = fileInfo.Length;
            if (newLength != length)
            {
                length = newLength;
            }
            else
            {
                break;
            }

            if (lastTimeLogInMs < 0 || (clock.ElapsedMilliseconds - lastTimeLogInMs) > options.UpdateLogAfterInMs)
            {
                options.WaitingFileToComplete?.Invoke(file);
                lastTimeLogInMs = clock.ElapsedMilliseconds;
            }

            if (clock.ElapsedMilliseconds - startTime > options.TimeOutAfterInMs)
            {
                options.WaitingFileToCompleteTimeOut?.Invoke(file);
                break;
            }

            await Task.Delay(options.CheckDeltaTimeInMs);
        }
    }

    public void Dispose()
    {
        _userSession?.Dispose();
        _userSession = null;
        _kernelSession?.Dispose();
        _kernelSession = null;
        _cleanCancel?.Dispose();
        _cleanCancel = null;
    }
}