using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Web.Script.Serialization;

namespace CodexHalo
{
#pragma warning disable 0649 // JavaScriptSerializer populates bridge DTO fields.
internal sealed class DeepSeekHarnessSnapshotDocument
    {
        public int schemaVersion;
        public string bridgeVersion;
        public string dshVersion;
        public string profile;
        public string instanceId;
        public int hostPid;
        public string hostStartUtc;
        public string hostExecutable;
        public int hostParentPid;
        public string hostRuntime;
        public string desktopAppId;
        public string hostProfileDir;
        public string hostRuntimeDir;
        public long revision;
        public string publishedAtUtc;
        public bool baselineReady;
        public DeepSeekHarnessCapabilities capabilities;
        public DeepSeekHarnessTask[] tasks;
    }

internal sealed class DeepSeekHarnessCapabilities
    {
        public bool lifecycle;
        public bool toolName;
        public bool attention;
        public bool subagents;
        public bool actualModel;
        public bool nextModel;
    }

internal sealed class DeepSeekHarnessTask
    {
        public string rootSessionId;
        public string title;
        public string turnId;
        public bool? running;
        public bool? rootRunning;
        public int? relatedRunning;
        public bool? relationsResolved;
        public int? activeToolCount;
        public int? pendingApprovalCount;
        public int? blockingQuestionCount;
        public string[] activeToolNames;
        public string lastActivityUtc;
        public DeepSeekHarnessModel mainModel;
        public DeepSeekHarnessModel nextModel;
        public DeepSeekHarnessTerminal terminal;
    }

internal sealed class DeepSeekHarnessModel
    {
        public string providerId;
        public string modelId;
        public string name;
        public string source;
        public string turnId;
        public string requestId;
        public string observedAtUtc;
    }

internal sealed class DeepSeekHarnessTerminal
    {
        public string turnId;
        public string kind;
        public string endedAtUtc;
    }
#pragma warning restore 0649

internal enum DeepSeekHarnessProcessCheck
    {
        Valid,
        Exited,
        Invalid,
        Unavailable
    }

internal sealed class DeepSeekHarnessProcessValidation
    {
        public DeepSeekHarnessProcessCheck Check;
        public DateTime ParentStartUtc;
        public bool BothProcessesExited;
        public string HostProcessName;
        public string ParentProcessName;
    }

internal sealed class DeepSeekHarnessProcessIdentity
    {
        public string HostExecutable;
        public string HostProcessName;
        public string ParentProcessName;
        public string HostProfileDir;
        public string HostRuntimeDir;
    }

internal enum DeepSeekHarnessProcessProbeResult
    {
        Found,
        NotFound,
        Unknown
    }

internal interface IDeepSeekHarnessProcessProbe
    {
        DeepSeekHarnessProcessProbeResult Probe(
            IList<DeepSeekHarnessProcessIdentity> identities);
    }

internal interface IDeepSeekHarnessProcessValidator
    {
        DeepSeekHarnessProcessValidation Validate(
            DeepSeekHarnessSnapshotDocument document);
    }

internal sealed class DeepSeekHarnessProcessValidator :
    IDeepSeekHarnessProcessValidator
    {
        public DeepSeekHarnessProcessValidation Validate(
            DeepSeekHarnessSnapshotDocument document)
        {
            Process host = null;
            Process parent = null;
            try
            {
                host = Process.GetProcessById(document.hostPid);
                parent = Process.GetProcessById(document.hostParentPid);
                DateTime expectedHostStart;
                if (!DeepSeekHarnessSnapshotReader.TryUtc(
                        document.hostStartUtc, out expectedHostStart))
                {
                    return Result(DeepSeekHarnessProcessCheck.Invalid,
                        DateTime.MinValue);
                }
                DateTime hostStart = host.StartTime.ToUniversalTime();
                DateTime parentStart = parent.StartTime.ToUniversalTime();
                string hostPath = Path.GetFullPath(
                    host.MainModule.FileName);
                string parentPath = Path.GetFullPath(
                    parent.MainModule.FileName);
                string expectedPath = Path.GetFullPath(
                    document.hostExecutable);
                if (Math.Abs((hostStart - expectedHostStart).TotalSeconds) > 2 ||
                    parentStart > hostStart.AddSeconds(2) ||
                    !String.Equals(hostPath, expectedPath,
                        StringComparison.OrdinalIgnoreCase) ||
                    !String.Equals(parentPath, expectedPath,
                        StringComparison.OrdinalIgnoreCase))
                {
                    return Result(DeepSeekHarnessProcessCheck.Invalid,
                        DateTime.MinValue);
                }
                return Result(DeepSeekHarnessProcessCheck.Valid,
                    parentStart, host.ProcessName, parent.ProcessName);
            }
            catch (ArgumentException)
            {
                return Result(DeepSeekHarnessProcessCheck.Exited,
                    DateTime.MinValue, null, null,
                    BothProcessesExited(document));
            }
            catch (InvalidOperationException)
            {
                return Result(DeepSeekHarnessProcessCheck.Exited,
                    DateTime.MinValue, null, null,
                    BothProcessesExited(document));
            }
            catch (System.ComponentModel.Win32Exception)
            {
                return Result(DeepSeekHarnessProcessCheck.Unavailable,
                    DateTime.MinValue);
            }
            catch (IOException)
            {
                return Result(DeepSeekHarnessProcessCheck.Unavailable,
                    DateTime.MinValue);
            }
            catch (UnauthorizedAccessException)
            {
                return Result(DeepSeekHarnessProcessCheck.Unavailable,
                    DateTime.MinValue);
            }
            catch (System.Security.SecurityException)
            {
                return Result(DeepSeekHarnessProcessCheck.Unavailable,
                    DateTime.MinValue);
            }
            catch (NotSupportedException)
            {
                return Result(DeepSeekHarnessProcessCheck.Unavailable,
                    DateTime.MinValue);
            }
            finally
            {
                if (parent != null) parent.Dispose();
                if (host != null) host.Dispose();
            }
        }

        private static DeepSeekHarnessProcessValidation Result(
            DeepSeekHarnessProcessCheck check, DateTime parentStartUtc,
            string hostProcessName = null, string parentProcessName = null,
            bool bothProcessesExited = false)
        {
            return new DeepSeekHarnessProcessValidation
            {
                Check = check,
                ParentStartUtc = parentStartUtc,
                HostProcessName = hostProcessName,
                ParentProcessName = parentProcessName,
                BothProcessesExited = bothProcessesExited
            };
        }

        private static bool BothProcessesExited(
            DeepSeekHarnessSnapshotDocument document)
        {
            return IsProcessDefinitelyGone(document.hostPid) &&
                IsProcessDefinitelyGone(document.hostParentPid);
        }

        private static bool IsProcessDefinitelyGone(int processId)
        {
            try
            {
                using (Process process = Process.GetProcessById(processId))
                {
                    return process.HasExited;
                }
            }
            catch (ArgumentException)
            {
                return true;
            }
            catch (InvalidOperationException)
            {
                return true;
            }
            catch (System.ComponentModel.Win32Exception)
            {
                return false;
            }
            catch (IOException)
            {
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }
            catch (System.Security.SecurityException)
            {
                return false;
            }
        }
    }

internal sealed class DeepSeekHarnessProcessProbe :
    IDeepSeekHarnessProcessProbe
    {
        private const uint SnapshotProcesses = 0x00000002;
        private const int ErrorNoMoreFiles = 18;
        private static readonly IntPtr InvalidHandle = new IntPtr(-1);

        public DeepSeekHarnessProcessProbeResult Probe(
            IList<DeepSeekHarnessProcessIdentity> identities)
        {
            if (identities == null || identities.Count == 0)
            {
                return DeepSeekHarnessProcessProbeResult.Unknown;
            }

            Dictionary<uint, ProcessEntry> processes =
                new Dictionary<uint, ProcessEntry>();
            if (!TryReadProcesses(processes))
            {
                return DeepSeekHarnessProcessProbeResult.Unknown;
            }

            bool unknown = false;
            foreach (DeepSeekHarnessProcessIdentity identity in identities)
            {
                if (identity == null ||
                    String.IsNullOrWhiteSpace(identity.HostExecutable) ||
                    String.IsNullOrWhiteSpace(identity.HostProcessName) ||
                    String.IsNullOrWhiteSpace(identity.ParentProcessName) ||
                    String.IsNullOrWhiteSpace(identity.HostProfileDir) ||
                    String.IsNullOrWhiteSpace(identity.HostRuntimeDir))
                {
                    unknown = true;
                    continue;
                }

                foreach (ProcessEntry hostEntry in processes.Values)
                {
                    if (!SameProcessName(hostEntry.ExecutableName,
                            identity.HostProcessName))
                    {
                        continue;
                    }

                    ProcessIdentityRead hostResult = ReadProcess(hostEntry,
                        identity.HostExecutable,
                        identity.HostProcessName);
                    if (hostResult.Result == ProcessReadResult.Different)
                    {
                        continue;
                    }
                    if (hostResult.Result == ProcessReadResult.Unknown)
                    {
                        unknown = true;
                        continue;
                    }

                    ProcessEntry parentEntry;
                    if (!processes.TryGetValue(hostEntry.ParentProcessId,
                            out parentEntry))
                    {
                        unknown = true;
                        continue;
                    }
                    if (!SameProcessName(parentEntry.ExecutableName,
                            identity.ParentProcessName))
                    {
                        continue;
                    }

                    ProcessIdentityRead parentRead = ReadProcessIdentity(
                        parentEntry);
                    if (parentRead.Result == ProcessReadResult.Unknown)
                    {
                        unknown = true;
                        continue;
                    }
                    if (parentRead.Result == ProcessReadResult.Different ||
                        !SameProcessName(parentRead.ProcessName,
                            identity.ParentProcessName) ||
                        !SamePath(parentRead.Executable,
                            identity.HostExecutable) ||
                        parentRead.StartUtc > hostResult.StartUtc.AddSeconds(2))
                    {
                        continue;
                    }

                    ProcessReadResult launchIdentity =
                        HasMatchingLaunchArguments(hostEntry.ProcessId,
                            identity);
                    if (launchIdentity == ProcessReadResult.Unknown)
                    {
                        unknown = true;
                        continue;
                    }
                    if (launchIdentity == ProcessReadResult.Different)
                    {
                        continue;
                    }
                    return DeepSeekHarnessProcessProbeResult.Found;
                }
            }

            return unknown
                ? DeepSeekHarnessProcessProbeResult.Unknown
                : DeepSeekHarnessProcessProbeResult.NotFound;
        }

        private static ProcessReadResult HasMatchingLaunchArguments(
            uint processId, DeepSeekHarnessProcessIdentity identity)
        {
            string commandLine;
            if (!TryReadCommandLine(processId, out commandLine) ||
                String.IsNullOrWhiteSpace(commandLine))
            {
                return ProcessReadResult.Unknown;
            }

            int argumentCount;
            IntPtr arguments = CommandLineToArgvW(commandLine,
                out argumentCount);
            if (arguments == IntPtr.Zero)
            {
                return ProcessReadResult.Unknown;
            }
            try
            {
                List<int> runtimeArguments = new List<int>();
                List<int> profileArguments = new List<int>();
                string expectedRuntime = NormalizePathForComparison(
                    identity.HostRuntimeDir);
                string expectedProfile = NormalizePathForComparison(
                    identity.HostProfileDir);
                for (int index = 0; index < argumentCount; index++)
                {
                    IntPtr argumentPointer = Marshal.ReadIntPtr(arguments,
                        index * IntPtr.Size);
                    string argument = Marshal.PtrToStringUni(
                        argumentPointer);
                    string fullPath;
                    if (!TryNormalizeAbsolutePath(argument, out fullPath))
                    {
                        continue;
                    }

                    if (String.Equals(fullPath, expectedRuntime,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        runtimeArguments.Add(index);
                    }
                    if (String.Equals(fullPath, expectedProfile,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        profileArguments.Add(index);
                    }
                }

                if (runtimeArguments.Count > 1 ||
                    profileArguments.Count > 1)
                {
                    return ProcessReadResult.Unknown;
                }
                if (runtimeArguments.Count == 1 &&
                    profileArguments.Count == 1)
                {
                    int runtimeIndex = runtimeArguments[0];
                    int profileIndex = profileArguments[0];
                    if (profileIndex == runtimeIndex + 1)
                    {
                        return ProcessReadResult.Match;
                    }
                    return ProcessReadResult.Unknown;
                }
                if (runtimeArguments.Count == 1)
                {
                    int nextIndex = runtimeArguments[0] + 1;
                    string nextPath;
                    if (nextIndex < argumentCount &&
                        TryNormalizeAbsolutePath(Marshal.PtrToStringUni(
                            Marshal.ReadIntPtr(arguments,
                                nextIndex * IntPtr.Size)), out nextPath))
                    {
                        return ProcessReadResult.Different;
                    }
                    return ProcessReadResult.Unknown;
                }
                if (profileArguments.Count == 1)
                {
                    int previousIndex = profileArguments[0] - 1;
                    string previousPath;
                    if (previousIndex >= 0 &&
                        TryNormalizeAbsolutePath(Marshal.PtrToStringUni(
                            Marshal.ReadIntPtr(arguments,
                                previousIndex * IntPtr.Size)),
                            out previousPath))
                    {
                        return ProcessReadResult.Different;
                    }
                }
                return ProcessReadResult.Unknown;
            }
            finally
            {
                LocalFree(arguments);
            }
        }

        private static bool TryNormalizeAbsolutePath(string value,
            out string normalized)
        {
            normalized = null;
            if (String.IsNullOrWhiteSpace(value) ||
                !Path.IsPathRooted(value))
            {
                return false;
            }
            try
            {
                normalized = NormalizePathForComparison(value);
                return true;
            }
            catch (ArgumentException)
            {
                return false;
            }
            catch (IOException)
            {
                return false;
            }
            catch (NotSupportedException)
            {
                return false;
            }
            catch (System.Security.SecurityException)
            {
                return false;
            }
        }

        private static string NormalizePathForComparison(string path)
        {
            string fullPath = Path.GetFullPath(path);
            string root = Path.GetPathRoot(fullPath);
            int minimumLength = String.IsNullOrEmpty(root) ? 0 : root.Length;
            while (fullPath.Length > minimumLength &&
                (fullPath[fullPath.Length - 1] ==
                    Path.DirectorySeparatorChar ||
                 fullPath[fullPath.Length - 1] ==
                    Path.AltDirectorySeparatorChar))
            {
                fullPath = fullPath.Substring(0, fullPath.Length - 1);
            }
            return fullPath;
        }

        private static bool TryReadCommandLine(uint processId,
            out string commandLine)
        {
            object locator = null;
            object services = null;
            object process = null;
            commandLine = null;
            try
            {
                Type locatorType = Type.GetTypeFromProgID(
                    "WbemScripting.SWbemLocator");
                if (locatorType == null)
                {
                    return false;
                }
                locator = Activator.CreateInstance(locatorType);
                services = locatorType.InvokeMember("ConnectServer",
                    BindingFlags.InvokeMethod | BindingFlags.Public |
                        BindingFlags.Instance, null, locator,
                    new object[] { ".", "root\\CIMV2" });
                if (services == null)
                {
                    return false;
                }
                string objectPath = "Win32_Process.Handle=\"" +
                    processId.ToString(CultureInfo.InvariantCulture) + "\"";
                process = services.GetType().InvokeMember("Get",
                    BindingFlags.InvokeMethod | BindingFlags.Public |
                        BindingFlags.Instance, null, services,
                    new object[] { objectPath });
                if (process == null)
                {
                    return false;
                }
                object value = process.GetType().InvokeMember("CommandLine",
                    BindingFlags.GetProperty | BindingFlags.Public |
                        BindingFlags.Instance, null, process, null);
                commandLine = value as string;
                return !String.IsNullOrWhiteSpace(commandLine);
            }
            catch
            {
                commandLine = null;
                return false;
            }
            finally
            {
                ReleaseComObject(process);
                ReleaseComObject(services);
                ReleaseComObject(locator);
            }
        }

        private static void ReleaseComObject(object value)
        {
            if (value != null && Marshal.IsComObject(value))
            {
                try
                {
                    Marshal.ReleaseComObject(value);
                }
                catch
                {
                }
            }
        }

        private static bool TryReadProcesses(
            Dictionary<uint, ProcessEntry> processes)
        {
            IntPtr snapshot = CreateToolhelp32Snapshot(SnapshotProcesses, 0);
            if (snapshot == IntPtr.Zero || snapshot == InvalidHandle)
            {
                return false;
            }

            try
            {
                ProcessEntry32 entry = new ProcessEntry32();
                entry.Size = (uint)Marshal.SizeOf(typeof(ProcessEntry32));
                if (!Process32First(snapshot, ref entry))
                {
                    return Marshal.GetLastWin32Error() == ErrorNoMoreFiles;
                }

                do
                {
                    processes[entry.ProcessId] = new ProcessEntry
                    {
                        ProcessId = entry.ProcessId,
                        ParentProcessId = entry.ParentProcessId,
                        ExecutableName = entry.ExecutableName
                    };
                    entry.Size = (uint)Marshal.SizeOf(
                        typeof(ProcessEntry32));
                }
                while (Process32Next(snapshot, ref entry));

                return Marshal.GetLastWin32Error() == ErrorNoMoreFiles;
            }
            finally
            {
                CloseHandle(snapshot);
            }
        }

        private static ProcessIdentityRead ReadProcess(ProcessEntry entry,
            string expectedExecutable, string expectedName)
        {
            ProcessIdentityRead identity = ReadProcessIdentity(entry);
            if (identity.Result != ProcessReadResult.Match)
            {
                return identity;
            }
            if (!SameProcessName(identity.ProcessName, expectedName) ||
                !SamePath(identity.Executable, expectedExecutable))
            {
                identity.Result = ProcessReadResult.Different;
            }
            return identity;
        }

        private static ProcessIdentityRead ReadProcessIdentity(
            ProcessEntry entry)
        {
            try
            {
                using (Process process = Process.GetProcessById(
                    (int)entry.ProcessId))
                {
                    string name = process.ProcessName;
                    string executable = Path.GetFullPath(
                        process.MainModule.FileName);
                    DateTime startUtc = process.StartTime.ToUniversalTime();
                    return new ProcessIdentityRead
                    {
                        Result = ProcessReadResult.Match,
                        ProcessName = name,
                        Executable = executable,
                        StartUtc = startUtc
                    };
                }
            }
            catch (ArgumentException)
            {
                return UnknownIdentity();
            }
            catch (InvalidOperationException)
            {
                return UnknownIdentity();
            }
            catch (System.ComponentModel.Win32Exception)
            {
                return UnknownIdentity();
            }
            catch (IOException)
            {
                return UnknownIdentity();
            }
            catch (UnauthorizedAccessException)
            {
                return UnknownIdentity();
            }
            catch (System.Security.SecurityException)
            {
                return UnknownIdentity();
            }
            catch (NotSupportedException)
            {
                return UnknownIdentity();
            }
        }

        private static ProcessIdentityRead UnknownIdentity()
        {
            return new ProcessIdentityRead
            {
                Result = ProcessReadResult.Unknown
            };
        }

        private static bool SameProcessName(string actual, string expected)
        {
            string actualName = Path.GetFileNameWithoutExtension(actual ??
                String.Empty);
            string expectedName = Path.GetFileNameWithoutExtension(
                expected ?? String.Empty);
            return String.Equals(actualName, expectedName,
                StringComparison.OrdinalIgnoreCase);
        }

        private static bool SamePath(string actual, string expected)
        {
            try
            {
                return String.Equals(Path.GetFullPath(actual),
                    Path.GetFullPath(expected),
                    StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        private enum ProcessReadResult
        {
            Match,
            Different,
            Unknown
        }

        private sealed class ProcessEntry
        {
            public uint ProcessId;
            public uint ParentProcessId;
            public string ExecutableName;
        }

        private sealed class ProcessIdentityRead
        {
            public ProcessReadResult Result;
            public string ProcessName;
            public string Executable;
            public DateTime StartUtc;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct ProcessEntry32
        {
            public uint Size;
            public uint UsageCount;
            public uint ProcessId;
            public IntPtr DefaultHeapId;
            public uint ModuleId;
            public uint ThreadCount;
            public uint ParentProcessId;
            public int BasePriority;
            public uint Flags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
            public string ExecutableName;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr CreateToolhelp32Snapshot(
            uint flags, uint processId);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode,
            EntryPoint = "Process32FirstW", SetLastError = true)]
        private static extern bool Process32First(IntPtr snapshot,
            ref ProcessEntry32 entry);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode,
            EntryPoint = "Process32NextW", SetLastError = true)]
        private static extern bool Process32Next(IntPtr snapshot,
            ref ProcessEntry32 entry);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr handle);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode,
            SetLastError = true)]
        private static extern IntPtr CommandLineToArgvW(string commandLine,
            out int argumentCount);

        [DllImport("kernel32.dll")]
        private static extern IntPtr LocalFree(IntPtr memory);
    }

internal sealed class DeepSeekHarnessInstanceSnapshot
    {
        public DeepSeekHarnessSnapshotDocument Document;
        public DateTime PublishedUtc;
        public DateTime ParentStartUtc;
        public bool Fresh;
    }

internal sealed class DeepSeekHarnessReadResult
    {
        public readonly List<DeepSeekHarnessInstanceSnapshot> Instances =
            new List<DeepSeekHarnessInstanceSnapshot>();
        public readonly HashSet<string> ConfirmedExitedInstanceIds =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        public AgentIntegrationState IntegrationState;
        public string StatusDetailKey;
        public DateTime LastEventUtc;
        public bool FilesPresent;
        public bool HadStoppedInstance;
        public bool Unsupported;
        public bool Broken;
        public bool UnverifiedStoppedInstance;
        public int ConfirmedExitedInstances;
    }

internal sealed class DeepSeekHarnessSnapshotReader
    {
        private enum DocumentValidationResult
        {
            Valid,
            Unsupported,
            Broken
        }

        internal const int MaxSnapshotBytes = 2 * 1024 * 1024;
        internal const int MaxTasksPerInstance = 512;
        internal const int MaxInstances = 32;
        internal static readonly TimeSpan RevisionTimeout =
            TimeSpan.FromSeconds(10);

        private readonly string runtimeDirectory;
        private readonly IDeepSeekHarnessProcessValidator processValidator;
        private readonly IDeepSeekHarnessProcessProbe processProbe;
        private readonly JavaScriptSerializer serializer;
        private readonly Dictionary<string, RevisionState> revisions =
            new Dictionary<string, RevisionState>(
                StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, VerifiedSnapshotIdentity>
            verifiedSnapshots = new Dictionary<string,
                VerifiedSnapshotIdentity>(StringComparer.OrdinalIgnoreCase);
        private readonly List<DeepSeekHarnessProcessIdentity>
            processIdentities = new List<DeepSeekHarnessProcessIdentity>();
        private bool hasSeenInstance;

        public DeepSeekHarnessSnapshotReader()
            : this(Path.Combine(SettingsStorage.AppDirectory, "integrations",
                "deepseek-harness", "runtime"),
                new DeepSeekHarnessProcessValidator())
        {
        }

        internal DeepSeekHarnessSnapshotReader(string directory,
            IDeepSeekHarnessProcessValidator validator)
            : this(directory, validator, new DeepSeekHarnessProcessProbe())
        {
        }

        internal DeepSeekHarnessSnapshotReader(string directory,
            IDeepSeekHarnessProcessValidator validator,
            IDeepSeekHarnessProcessProbe restartProbe)
        {
            if (validator == null)
            {
                throw new ArgumentNullException("validator");
            }
            if (restartProbe == null)
            {
                throw new ArgumentNullException("restartProbe");
            }
            runtimeDirectory = Path.GetFullPath(directory);
            processValidator = validator;
            processProbe = restartProbe;
            serializer = new JavaScriptSerializer();
            serializer.MaxJsonLength = MaxSnapshotBytes;
        }

        internal bool CanStopSnapshotPolling(
            DeepSeekHarnessReadResult result)
        {
            return hasSeenInstance && processIdentities.Count > 0 &&
                result != null && result.HadStoppedInstance &&
                result.ConfirmedExitedInstances > 0 &&
                !result.UnverifiedStoppedInstance && !result.Broken &&
                !result.Unsupported && result.Instances.Count == 0 &&
                revisions.Count == 0;
        }

        internal DeepSeekHarnessProcessProbeResult ProbeForRestart()
        {
            return processProbe.Probe(processIdentities);
        }

        public DeepSeekHarnessReadResult Read(DateTime nowUtc)
        {
            return Read(nowUtc, Stopwatch.GetTimestamp());
        }

        internal DeepSeekHarnessReadResult Read(DateTime nowUtc,
            long monotonicTicks)
        {
            DeepSeekHarnessReadResult result = new DeepSeekHarnessReadResult();
            if (!Directory.Exists(runtimeDirectory))
            {
                AddCachedInstances(result);
                SetHealth(result);
                return result;
            }

            string[] files;
            try
            {
                files = Directory.EnumerateFiles(runtimeDirectory, "*.json")
                    .Take(MaxInstances + 1).ToArray();
            }
            catch (IOException)
            {
                AddCachedInstances(result);
                result.Broken = true;
                SetHealth(result);
                return result;
            }
            catch (UnauthorizedAccessException)
            {
                AddCachedInstances(result);
                result.Broken = true;
                SetHealth(result);
                return result;
            }
            catch (System.Security.SecurityException)
            {
                AddCachedInstances(result);
                result.Broken = true;
                SetHealth(result);
                return result;
            }
            Array.Sort(files, StringComparer.OrdinalIgnoreCase);
            result.FilesPresent = files.Length > 0;
            if (files.Length > MaxInstances)
            {
                result.Broken = true;
                result.IntegrationState = AgentIntegrationState.Broken;
                result.StatusDetailKey = "status.deepseek.unknown";
                return result;
            }
            PruneVerifiedSnapshots(files);
            HashSet<string> activeInstances = new HashSet<string>(
                StringComparer.OrdinalIgnoreCase);
            bool parentIdentityChanged = false;
            foreach (string file in files)
            {
                DeepSeekHarnessSnapshotDocument document;
                if (!TryReadDocument(file, out document))
                {
                    result.Broken = true;
                    continue;
                }
                DocumentValidationResult validationResult =
                    ValidateDocument(file, document);
                if (validationResult != DocumentValidationResult.Valid)
                {
                    if (validationResult ==
                        DocumentValidationResult.Unsupported)
                    {
                        result.Unsupported = true;
                    }
                    else
                    {
                        result.Broken = true;
                    }
                    continue;
                }

                DeepSeekHarnessProcessValidation validation =
                    processValidator.Validate(document);
                if (validation == null || validation.Check ==
                        DeepSeekHarnessProcessCheck.Unavailable)
                {
                    result.Broken = true;
                    continue;
                }
                if (validation.Check != DeepSeekHarnessProcessCheck.Valid)
                {
                    if (validation.Check == DeepSeekHarnessProcessCheck.Exited)
                    {
                        result.HadStoppedInstance = true;
                        bool verified = HasVerifiedSnapshot(document);
                        if (!verified)
                        {
                            result.UnverifiedStoppedInstance = true;
                        }
                        else if (validation.BothProcessesExited)
                        {
                            ConfirmExitedInstance(result, document.instanceId);
                        }
                    }
                    else
                    {
                        result.Broken = true;
                    }
                    continue;
                }

                hasSeenInstance = true;
                RememberProcessIdentity(document, validation);
                RememberVerifiedSnapshot(document);
                activeInstances.Add(document.instanceId);
                DateTime publishedUtc;
                TryUtc(document.publishedAtUtc, out publishedUtc);
                RevisionState previousState;
                if (revisions.TryGetValue(document.instanceId,
                        out previousState) &&
                    previousState.Document.hostPid == document.hostPid &&
                    String.Equals(previousState.Document.hostStartUtc,
                        document.hostStartUtc, StringComparison.Ordinal) &&
                    previousState.ParentStartUtc != DateTime.MinValue &&
                    Math.Abs((previousState.ParentStartUtc -
                        validation.ParentStartUtc).TotalSeconds) > 2)
                {
                    parentIdentityChanged = true;
                    revisions.Remove(document.instanceId);
                    continue;
                }
                RevisionState state = GetRevisionState(document,
                    publishedUtc, nowUtc, monotonicTicks,
                    validation.ParentStartUtc);
                result.Instances.Add(new DeepSeekHarnessInstanceSnapshot
                {
                    Document = state.Document,
                    PublishedUtc = state.PublishedUtc,
                    ParentStartUtc = state.ParentStartUtc,
                    Fresh = state.LastProgressTicks != 0 &&
                        Elapsed(state.LastProgressTicks, monotonicTicks) <=
                            RevisionTimeout
                });
                if (state.PublishedUtc > result.LastEventUtc)
                {
                    result.LastEventUtc = state.PublishedUtc;
                }
            }

            foreach (string instanceId in revisions.Keys.ToList())
            {
                if (!activeInstances.Contains(instanceId))
                {
                    RevisionState cached = revisions[instanceId];
                    bool replacedBySameHost = result.Instances.Any(
                        delegate(DeepSeekHarnessInstanceSnapshot current)
                        {
                            DeepSeekHarnessSnapshotDocument oldDocument =
                                cached.Document;
                            DeepSeekHarnessSnapshotDocument newDocument =
                                current.Document;
                            return oldDocument.hostPid == newDocument.hostPid &&
                                oldDocument.hostParentPid ==
                                    newDocument.hostParentPid &&
                                SameHostStart(oldDocument, newDocument) &&
                                String.Equals(oldDocument.hostExecutable,
                                    newDocument.hostExecutable,
                                    StringComparison.OrdinalIgnoreCase) &&
                                cached.ParentStartUtc != DateTime.MinValue &&
                                current.ParentStartUtc != DateTime.MinValue &&
                                Math.Abs((cached.ParentStartUtc -
                                    current.ParentStartUtc).TotalSeconds) <= 2;
                        });
                    if (replacedBySameHost)
                    {
                        revisions.Remove(instanceId);
                        continue;
                    }
                    AddCachedInstance(result, instanceId, cached);
                }
            }

            if (parentIdentityChanged)
            {
                result.Broken = true;
            }
            SetHealth(result);
            return result;
        }

        private void AddCachedInstances(DeepSeekHarnessReadResult result)
        {
            foreach (string instanceId in revisions.Keys.ToList())
            {
                RevisionState cached = revisions[instanceId];
                AddCachedInstance(result, instanceId, cached);
            }
        }

        private void AddCachedInstance(DeepSeekHarnessReadResult result,
            string instanceId, RevisionState cached)
        {
            DeepSeekHarnessProcessValidation validation =
                processValidator.Validate(cached.Document);
            if (validation != null && validation.Check ==
                    DeepSeekHarnessProcessCheck.Valid &&
                (cached.ParentStartUtc == DateTime.MinValue ||
                    Math.Abs((cached.ParentStartUtc -
                        validation.ParentStartUtc).TotalSeconds) <= 2))
            {
                result.Instances.Add(new DeepSeekHarnessInstanceSnapshot
                {
                    Document = cached.Document,
                    PublishedUtc = cached.PublishedUtc,
                    ParentStartUtc = cached.ParentStartUtc,
                    Fresh = false
                });
                if (cached.PublishedUtc > result.LastEventUtc)
                {
                    result.LastEventUtc = cached.PublishedUtc;
                }
            }
            else
            {
                if (validation != null && validation.Check ==
                    DeepSeekHarnessProcessCheck.Exited)
                {
                    result.HadStoppedInstance = true;
                    if (validation.BothProcessesExited)
                    {
                        revisions.Remove(instanceId);
                        ConfirmExitedInstance(result, instanceId);
                    }
                }
                else
                {
                    result.Broken = true;
                }
            }
        }

        private bool HasVerifiedSnapshot(
            DeepSeekHarnessSnapshotDocument document)
        {
            VerifiedSnapshotIdentity state;
            return document != null &&
                verifiedSnapshots.TryGetValue(document.instanceId,
                    out state) && state.Matches(document);
        }

        private void RememberVerifiedSnapshot(
            DeepSeekHarnessSnapshotDocument document)
        {
            if (document == null ||
                String.IsNullOrWhiteSpace(document.instanceId))
            {
                return;
            }
            verifiedSnapshots[document.instanceId] =
                new VerifiedSnapshotIdentity(document);
        }

        private void PruneVerifiedSnapshots(string[] files)
        {
            HashSet<string> presentInstances = new HashSet<string>(
                files.Select(delegate(string file)
                {
                    return Path.GetFileNameWithoutExtension(file);
                }), StringComparer.OrdinalIgnoreCase);
            foreach (string instanceId in verifiedSnapshots.Keys.ToList())
            {
                if (!presentInstances.Contains(instanceId))
                {
                    verifiedSnapshots.Remove(instanceId);
                }
            }
        }

        private static void ConfirmExitedInstance(
            DeepSeekHarnessReadResult result, string instanceId)
        {
            if (!String.IsNullOrWhiteSpace(instanceId) &&
                result.ConfirmedExitedInstanceIds.Add(instanceId))
            {
                result.ConfirmedExitedInstances++;
            }
        }

        private void RememberProcessIdentity(
            DeepSeekHarnessSnapshotDocument document,
            DeepSeekHarnessProcessValidation validation)
        {
            if (String.IsNullOrWhiteSpace(validation.HostProcessName) ||
                String.IsNullOrWhiteSpace(validation.ParentProcessName))
            {
                return;
            }
            foreach (DeepSeekHarnessProcessIdentity previous in
                processIdentities)
            {
                if (SameFullPath(previous.HostExecutable,
                        document.hostExecutable) &&
                    String.Equals(previous.HostProcessName,
                        validation.HostProcessName,
                        StringComparison.OrdinalIgnoreCase) &&
                    String.Equals(previous.ParentProcessName,
                        validation.ParentProcessName,
                        StringComparison.OrdinalIgnoreCase) &&
                    SameFullPath(previous.HostProfileDir,
                        document.hostProfileDir) &&
                    SameFullPath(previous.HostRuntimeDir,
                        document.hostRuntimeDir))
                {
                    return;
                }
            }
            processIdentities.Add(new DeepSeekHarnessProcessIdentity
            {
                HostExecutable = Path.GetFullPath(document.hostExecutable),
                HostProcessName = validation.HostProcessName,
                ParentProcessName = validation.ParentProcessName,
                HostProfileDir = Path.GetFullPath(document.hostProfileDir),
                HostRuntimeDir = Path.GetFullPath(document.hostRuntimeDir)
            });
        }

        private RevisionState GetRevisionState(
            DeepSeekHarnessSnapshotDocument document,
            DateTime publishedUtc, DateTime nowUtc, long monotonicTicks,
            DateTime parentStartUtc)
        {
            RevisionState state;
            if (!revisions.TryGetValue(document.instanceId, out state) ||
                state.Document.hostPid != document.hostPid ||
                !String.Equals(state.Document.hostStartUtc,
                    document.hostStartUtc, StringComparison.Ordinal) ||
                !String.Equals(state.Document.hostExecutable,
                    document.hostExecutable,
                    StringComparison.OrdinalIgnoreCase))
            {
                state = new RevisionState();
                revisions[document.instanceId] = state;
            }

            if (state.Document == null)
            {
                state.Document = document;
                state.PublishedUtc = publishedUtc;
                state.ParentStartUtc = parentStartUtc;
                if (IsRecent(publishedUtc, nowUtc))
                {
                    state.LastProgressTicks = monotonicTicks;
                }
            }
            else if (document.revision > state.Document.revision &&
                IsRecent(publishedUtc, nowUtc))
            {
                state.Document = document;
                state.PublishedUtc = publishedUtc;
                state.ParentStartUtc = parentStartUtc;
                state.LastProgressTicks = monotonicTicks;
            }
            return state;
        }

        private bool TryReadDocument(string file,
            out DeepSeekHarnessSnapshotDocument document)
        {
            document = null;
            try
            {
                using (FileStream stream = new FileStream(file,
                    FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete))
                using (MemoryStream buffer = new MemoryStream())
                {
                    if (stream.Length > MaxSnapshotBytes)
                    {
                        return false;
                    }
                    byte[] chunk = new byte[8192];
                    int read;
                    while ((read = stream.Read(chunk, 0, chunk.Length)) > 0)
                    {
                        if (buffer.Length + read > MaxSnapshotBytes)
                        {
                            return false;
                        }
                        buffer.Write(chunk, 0, read);
                    }
                    string json = Encoding.UTF8.GetString(buffer.ToArray());
                    document = serializer.Deserialize<
                        DeepSeekHarnessSnapshotDocument>(json);
                    if (document == null)
                    {
                        return false;
                    }
                    return true;
                }
            }
            catch
            {
                return false;
            }
        }

        private DocumentValidationResult ValidateDocument(string file,
            DeepSeekHarnessSnapshotDocument document)
        {
            if (document == null)
            {
                return DocumentValidationResult.Broken;
            }
            if (document.schemaVersion != 1 ||
                !IsSupportedBridgeVersion(document.bridgeVersion) ||
                !String.Equals(document.dshVersion, "0.2.1-rc.2",
                    StringComparison.Ordinal) ||
                !String.Equals(document.profile, "desktop",
                    StringComparison.Ordinal) ||
                !String.Equals(document.desktopAppId,
                    "com.deepseek.dsh", StringComparison.Ordinal))
            {
                return DocumentValidationResult.Unsupported;
            }

            if (!String.Equals(document.hostRuntime, "electron-node",
                    StringComparison.Ordinal) ||
                String.IsNullOrWhiteSpace(document.instanceId) ||
                document.hostPid <= 0 || document.hostParentPid <= 0 ||
                document.hostPid == document.hostParentPid ||
                String.IsNullOrWhiteSpace(document.hostExecutable) ||
                String.IsNullOrWhiteSpace(document.hostProfileDir) ||
                String.IsNullOrWhiteSpace(document.hostRuntimeDir) ||
                !Path.IsPathRooted(document.hostExecutable) ||
                !Path.IsPathRooted(document.hostRuntimeDir) ||
                document.revision < 0 || document.capabilities == null ||
                document.tasks == null ||
                document.tasks.Length > MaxTasksPerInstance)
            {
                return DocumentValidationResult.Broken;
            }

            string expectedFile = document.instanceId + ".json";
            if (!String.Equals(Path.GetFileName(file), expectedFile,
                    StringComparison.OrdinalIgnoreCase))
            {
                return DocumentValidationResult.Broken;
            }

            DateTime ignored;
            if (!TryUtc(document.hostStartUtc, out ignored) ||
                !TryUtc(document.publishedAtUtc, out ignored))
            {
                return DocumentValidationResult.Broken;
            }

            string expectedProfile =
                AgentHaloPaths.DeepSeekHarnessDesktopProfileDirectory();
            if (!SameFullPath(document.hostProfileDir, expectedProfile))
            {
                return DocumentValidationResult.Broken;
            }

            HashSet<string> roots = new HashSet<string>(
                StringComparer.Ordinal);
            foreach (DeepSeekHarnessTask task in document.tasks)
            {
                if (task == null ||
                    String.IsNullOrWhiteSpace(task.rootSessionId) ||
                    !roots.Add(task.rootSessionId))
                {
                    return DocumentValidationResult.Broken;
                }
            }
            return DocumentValidationResult.Valid;
        }

        private static bool IsSupportedBridgeVersion(string value)
        {
            if (String.IsNullOrWhiteSpace(value))
            {
                return false;
            }
            string[] parts = value.Split('-')[0].Split('.');
            int major;
            return parts.Length > 0 && Int32.TryParse(parts[0], out major) &&
                major == 1;
        }

        private void SetHealth(DeepSeekHarnessReadResult result)
        {
            if (result.Broken || result.Unsupported)
            {
                result.IntegrationState = AgentIntegrationState.Broken;
                result.StatusDetailKey = result.Unsupported
                    ? "status.deepseek.unsupported"
                    : "status.deepseek.unknown";
                return;
            }

            if (result.Instances.Count == 0)
            {
                if (result.HadStoppedInstance || result.FilesPresent ||
                    hasSeenInstance)
                {
                    result.IntegrationState = AgentIntegrationState.NotRequired;
                    result.StatusDetailKey = "status.deepseek.not_running";
                }
                else
                {
                    result.IntegrationState = AgentIntegrationState.NotConfigured;
                    result.StatusDetailKey = "status.deepseek.not_configured";
                }
                return;
            }

            if (result.Instances.Any(delegate(
                    DeepSeekHarnessInstanceSnapshot instance)
                {
                    return !instance.Fresh;
                }))
            {
                result.IntegrationState = AgentIntegrationState.Stale;
                result.StatusDetailKey = "status.deepseek.disconnected";
            }
            else if (result.Instances.Any(delegate(
                    DeepSeekHarnessInstanceSnapshot instance)
                {
                    return !instance.Document.baselineReady;
                }))
            {
                result.IntegrationState = AgentIntegrationState.Stale;
                result.StatusDetailKey = "status.deepseek.connecting";
            }
            else
            {
                result.IntegrationState = AgentIntegrationState.Healthy;
                result.StatusDetailKey = "status.deepseek.ready";
            }
        }

        internal static bool TryUtc(string value, out DateTime utc)
        {
            DateTime parsed;
            if (!DateTime.TryParse(value, CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind, out parsed) ||
                String.IsNullOrEmpty(value) ||
                !value.EndsWith("Z", StringComparison.OrdinalIgnoreCase))
            {
                utc = DateTime.MinValue;
                return false;
            }
            utc = parsed.ToUniversalTime();
            return true;
        }

        private static bool SameHostStart(
            DeepSeekHarnessSnapshotDocument first,
            DeepSeekHarnessSnapshotDocument second)
        {
            DateTime firstStart;
            DateTime secondStart;
            return TryUtc(first.hostStartUtc, out firstStart) &&
                TryUtc(second.hostStartUtc, out secondStart) &&
                Math.Abs((firstStart - secondStart).TotalSeconds) <= 2;
        }

        private static bool SameFullPath(string first, string second)
        {
            try
            {
                string a = Path.GetFullPath(first).TrimEnd(
                    Path.DirectorySeparatorChar,
                    Path.AltDirectorySeparatorChar);
                string b = Path.GetFullPath(second).TrimEnd(
                    Path.DirectorySeparatorChar,
                    Path.AltDirectorySeparatorChar);
                return String.Equals(a, b,
                    StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        private static bool IsRecent(DateTime publishedUtc, DateTime nowUtc)
        {
            TimeSpan age = nowUtc.ToUniversalTime() - publishedUtc;
            return age >= TimeSpan.FromSeconds(-2) &&
                age <= RevisionTimeout;
        }

        private static TimeSpan Elapsed(long startTicks, long endTicks)
        {
            if (endTicks < startTicks)
            {
                return TimeSpan.MaxValue;
            }
            return TimeSpan.FromSeconds((endTicks - startTicks) /
                (double)Stopwatch.Frequency);
        }

        private sealed class RevisionState
        {
            public DeepSeekHarnessSnapshotDocument Document;
            public DateTime PublishedUtc;
            public DateTime ParentStartUtc;
            public long LastProgressTicks;
        }

        private sealed class VerifiedSnapshotIdentity
        {
            private readonly int hostPid;
            private readonly int hostParentPid;
            private readonly string hostStartUtc;
            private readonly string hostExecutable;

            public VerifiedSnapshotIdentity(
                DeepSeekHarnessSnapshotDocument document)
            {
                hostPid = document.hostPid;
                hostParentPid = document.hostParentPid;
                hostStartUtc = document.hostStartUtc;
                hostExecutable = document.hostExecutable;
            }

            public bool Matches(DeepSeekHarnessSnapshotDocument document)
            {
                return document != null && hostPid == document.hostPid &&
                    hostParentPid == document.hostParentPid &&
                    String.Equals(hostStartUtc, document.hostStartUtc,
                        StringComparison.Ordinal) &&
                    String.Equals(hostExecutable, document.hostExecutable,
                        StringComparison.OrdinalIgnoreCase);
            }
        }
    }

internal sealed class DeepSeekHarnessTaskView
    {
        public DeepSeekHarnessTask Task;
        public HaloState State;
        public int Priority;
        public DateTime ActivityUtc;
        public string StatusDetailKey;
        public string ModelName;
        public string ModelProvider;
        public string ModelNameKey;
        public string ModelSourceKey;
        public bool HasTitle;
        public string Title;
        public DateTime CompletedUtc;
        public DateTime LastEventUtc;
        public AgentTurnPhase TurnPhase;
        public AgentActivityKind Activity;
        public AgentAttentionReason AttentionReason;
        public AgentFailureSeverity FailureSeverity;
        public string EvidenceKind;
        public bool Active;
    }

internal static class DeepSeekHarnessSnapshotReducer
    {
        public static AgentProviderSnapshot Build(
            DeepSeekHarnessReadResult source, HaloSettings settings,
            DateTime nowUtc, string previousRootId,
            out string selectedRootId)
        {
            settings = settings ?? new HaloSettings();
            if (settings.Paused)
            {
                selectedRootId = previousRootId ?? String.Empty;
                return BuildPaused(null, source);
            }
            List<DeepSeekHarnessTaskView> views = BuildTaskViews(source,
                settings, nowUtc);
            DeepSeekHarnessTaskView selected = SelectTaskAndOrder(views,
                previousRootId);
            selectedRootId = selected == null ? String.Empty :
                selected.Task.rootSessionId;

            List<SessionSnapshot> sessions = new List<SessionSnapshot>();
            foreach (DeepSeekHarnessTaskView view in views)
            {
                sessions.Add(ToSession(view));
            }

            bool confirmedReady = IsReady(source);
            bool hasLiveInstance = source != null &&
                source.Instances.Count > 0;
            bool unknown = selected != null && selected.State == HaloState.Idle &&
                selected.Priority == 1;
            if (!confirmedReady && selected != null &&
                (selected.State == HaloState.Done ||
                    selected.State == HaloState.Idle))
            {
                unknown = true;
            }
            bool connectedUnknown = hasLiveInstance &&
                (!confirmedReady || unknown);

            HaloState aggregateState;
            string aggregateLabel;
            string statusKey;
            AgentPresenceState presence;
            AgentTurnPhase phase = AgentTurnPhase.None;
            AgentActivityKind activity = AgentActivityKind.None;
            AgentAttentionReason attention = AgentAttentionReason.None;
            AgentFailureSeverity failure = AgentFailureSeverity.None;
            AgentEvidenceSource evidence = AgentEvidenceSource.DeepSeekHarnessBridge;
            string evidenceKind = "deepseek_harness_status";

            if (selected != null && !unknown)
            {
                if (selected.State == HaloState.Idle)
                {
                    aggregateState = HaloState.Done;
                    aggregateLabel = "STANDBY";
                    statusKey = "status.deepseek.standby";
                    presence = AgentPresenceState.Standby;
                }
                else
                {
                    aggregateState = selected.State;
                    aggregateLabel = GeneratedHaloSpec.State(
                        selected.State).Label;
                    statusKey = selected.StatusDetailKey;
                    presence = AgentPresenceState.Active;
                    phase = selected.TurnPhase;
                    activity = selected.Activity;
                    attention = selected.AttentionReason;
                    failure = selected.FailureSeverity;
                    evidenceKind = selected.EvidenceKind;
                }
            }
            else if (selected != null || connectedUnknown || source != null &&
                source.Broken || source != null && source.Unsupported)
            {
                aggregateState = HaloState.Idle;
                aggregateLabel = "UNKNOWN";
                statusKey = selected != null
                    ? source != null && source.StatusDetailKey ==
                        "status.deepseek.disconnected"
                        ? "status.deepseek.disconnected"
                        : "status.deepseek.unknown"
                    : source != null && source.Unsupported
                        ? "status.deepseek.unsupported"
                        : connectedUnknown && (source == null ||
                            source.StatusDetailKey == "status.deepseek.ready")
                            ? "status.deepseek.unknown"
                        : source != null && source.StatusDetailKey != null
                            ? source.StatusDetailKey
                            : "status.deepseek.unknown";
                presence = hasLiveInstance ? AgentPresenceState.Active
                    : AgentPresenceState.Offline;
            }
            else if (source != null && source.IntegrationState ==
                AgentIntegrationState.Healthy && confirmedReady)
            {
                aggregateState = HaloState.Done;
                aggregateLabel = "STANDBY";
                statusKey = "status.deepseek.standby";
                presence = AgentPresenceState.Standby;
            }
            else
            {
                aggregateState = HaloState.Idle;
                aggregateLabel = source != null &&
                    source.StatusDetailKey == "status.deepseek.not_configured"
                    ? "UNKNOWN" : GeneratedHaloSpec.State(
                        HaloState.Idle).Label;
                statusKey = source == null
                    ? "status.deepseek.unknown"
                    : source.StatusDetailKey;
                presence = AgentPresenceState.Offline;
                evidence = AgentEvidenceSource.None;
                evidenceKind = String.Empty;
            }

            string title = selected == null ? String.Empty : selected.Title;
            string titleKey = selected != null && selected.HasTitle
                ? String.Empty
                : selected != null ? "details.deepseek.title.unnamed"
                    : TitlePlaceholder(source);
            string modelName = selected == null ? String.Empty :
                selected.ModelName;
            string modelNameKey = selected != null &&
                !String.IsNullOrWhiteSpace(selected.ModelName)
                ? String.Empty
                : source != null && source.StatusDetailKey ==
                    "status.deepseek.connecting"
                    ? "details.deepseek.model.loading"
                    : selected != null ? "details.deepseek.model.not_fetched"
                        : ModelPlaceholder();
            return new AgentProviderSnapshot
            {
                Aggregate = new AggregateSnapshot
                {
                    State = aggregateState,
                    Label = aggregateLabel,
                    Detail = L10n.Instance[statusKey],
                    Sessions = sessions,
                    FocusedAgent = AgentKind.DeepSeekHarness,
                    Presence = presence,
                    TurnPhase = phase,
                    Activity = activity,
                    EvidenceSource = evidence,
                    EvidenceKind = evidenceKind,
                    AttentionReason = attention,
                    FailureSeverity = failure
                },
                Sessions = sessions,
                Details = new AgentDetailsSnapshot
                {
                    Agent = AgentKind.DeepSeekHarness,
                    Mode = AgentDetailsMode.DeepSeekTask,
                    TaskTitle = title,
                    TaskTitleKey = titleKey,
                    ModelName = modelName,
                    ModelNameKey = modelNameKey,
                    ModelSourceKey = selected == null ? String.Empty :
                        selected.ModelSourceKey,
                    StatusDetailKey = statusKey,
                    ProviderName = selected == null ? String.Empty :
                        selected.ModelProvider,
                    Usage = null
                },
                Capabilities = SnapshotCapabilities(source),
                Integration = new AgentIntegrationHealth
                {
                    State = source == null
                        ? AgentIntegrationState.Broken
                        : source.IntegrationState,
                    DetailKey = statusKey,
                    LastEventUtc = source == null ? DateTime.MinValue :
                        source.LastEventUtc
                }
            };
        }

        internal static AgentProviderSnapshot BuildPaused(
            AgentProviderSnapshot previous, DeepSeekHarnessReadResult source)
        {
            AgentDetailsSnapshot oldDetails = previous == null
                ? null : previous.Details;
            string title = oldDetails == null ? String.Empty :
                oldDetails.TaskTitle ?? String.Empty;
            string titleKey = oldDetails == null
                ? "details.deepseek.title.unavailable"
                : oldDetails.TaskTitleKey;
            string model = oldDetails == null ? String.Empty :
                oldDetails.ModelName ?? String.Empty;
            string modelKey = oldDetails == null
                ? "details.deepseek.model.unavailable"
                : oldDetails.ModelNameKey;
            string sourceKey = oldDetails == null ? String.Empty :
                oldDetails.ModelSourceKey;
            List<SessionSnapshot> sessions = new List<SessionSnapshot>();
            return new AgentProviderSnapshot
            {
                Aggregate = new AggregateSnapshot
                {
                    State = HaloState.Idle,
                    Label = "PAUSED",
                    Detail = L10n.Instance["status.paused"],
                    Sessions = sessions,
                    FocusedAgent = AgentKind.DeepSeekHarness,
                    Presence = AgentPresenceState.Offline,
                    EvidenceSource = AgentEvidenceSource.None
                },
                Sessions = sessions,
                Details = new AgentDetailsSnapshot
                {
                    Agent = AgentKind.DeepSeekHarness,
                    Mode = AgentDetailsMode.DeepSeekTask,
                    TaskTitle = title,
                    TaskTitleKey = titleKey,
                    ModelName = model,
                    ModelNameKey = modelKey,
                    ModelSourceKey = sourceKey,
                    StatusDetailKey = "status.paused"
                },
                Capabilities = previous == null
                    ? AgentCapability.None : previous.Capabilities,
                Integration = previous == null || previous.Integration == null
                    ? new AgentIntegrationHealth
                    {
                        State = source == null
                            ? AgentIntegrationState.NotConfigured
                            : source.IntegrationState,
                        DetailKey = "status.paused"
                    }
                    : previous.Integration
            };
        }

        private static string TitlePlaceholder(
            DeepSeekHarnessReadResult source)
        {
            string status = source == null ? String.Empty :
                source.StatusDetailKey;
            if (status == "status.deepseek.connecting")
                return "details.deepseek.title.loading";
            if (status == "status.deepseek.not_running")
                return "details.deepseek.title.no_recent_task";
            if (status == "status.deepseek.not_configured" ||
                status == "status.deepseek.unsupported" ||
                status == "status.deepseek.unknown")
                return "details.deepseek.title.unavailable";
            return "details.deepseek.title.no_task";
        }

        private static string ModelPlaceholder()
        {
            return "details.deepseek.model.unavailable";
        }

        private static AgentCapability SnapshotCapabilities(
            DeepSeekHarnessReadResult source)
        {
            if (source == null || source.Instances.Count == 0 ||
                source.IntegrationState != AgentIntegrationState.Healthy)
            {
                return AgentCapability.None;
            }
            AgentCapability capabilities = SupportedSnapshotCapabilities;
            foreach (DeepSeekHarnessInstanceSnapshot instance in
                source.Instances)
            {
                DeepSeekHarnessCapabilities flags =
                    instance.Document.capabilities;
                if (flags == null ||
                    !instance.Fresh || !instance.Document.baselineReady)
                {
                    return AgentCapability.None;
                }
                if (!flags.lifecycle)
                    capabilities &= ~AgentCapability.Lifecycle;
                if (!flags.toolName)
                    capabilities &= ~AgentCapability.ToolName;
                if (!flags.attention)
                    capabilities &= ~AgentCapability.Attention;
            }
            return capabilities;
        }

        private const AgentCapability SupportedSnapshotCapabilities =
            AgentCapability.Lifecycle | AgentCapability.ToolName |
            AgentCapability.Attention;

        private static List<DeepSeekHarnessTaskView> BuildTaskViews(
            DeepSeekHarnessReadResult source, HaloSettings settings,
            DateTime nowUtc)
        {
            List<DeepSeekHarnessTaskView> views = new List<
                DeepSeekHarnessTaskView>();
            if (source == null)
            {
                return views;
            }

            List<TaskSource> flattened = source.Instances.SelectMany(
                delegate(DeepSeekHarnessInstanceSnapshot instance)
                {
                    if (!instance.Document.baselineReady)
                    {
                        return Enumerable.Empty<TaskSource>();
                    }
                    return instance.Document.tasks.Select(
                        delegate(DeepSeekHarnessTask task)
                        {
                            return new TaskSource(task, instance);
                        });
                }).ToList();
            foreach (IGrouping<string, TaskSource> group in flattened.GroupBy(
                delegate(TaskSource item)
                {
                    return item.Task.rootSessionId;
                }, StringComparer.Ordinal))
            {
                TaskSource item = group.OrderBy(delegate(TaskSource value)
                    {
                        return value.Instance.Document.instanceId;
                    }, StringComparer.Ordinal).First();
                DeepSeekHarnessTaskView view = CreateTaskView(item.Task,
                    item.Instance, settings, nowUtc, group.Count() > 1);
                views.Add(view);
            }
            return views;
        }

        private static DeepSeekHarnessTaskView CreateTaskView(
            DeepSeekHarnessTask task,
            DeepSeekHarnessInstanceSnapshot instance,
            HaloSettings settings, DateTime nowUtc, bool ambiguous)
        {
            DeepSeekHarnessTaskView view = new DeepSeekHarnessTaskView
            {
                Task = task,
                HasTitle = !String.IsNullOrWhiteSpace(task.title),
                Title = (task.title ?? String.Empty).Trim(),
                ActivityUtc = ReadActivity(task, nowUtc),
                EvidenceKind = "deepseek_harness_task"
            };

            if (ambiguous || !instance.Fresh ||
                !CanClassify(instance.Document, task))
            {
                SetUnknown(view);
                ResolveModel(view, instance.Document, task, true);
                return view;
            }

            int approvals = task.pendingApprovalCount.Value;
            int questions = task.blockingQuestionCount.Value;
            int tools = task.activeToolCount.Value;
            bool running = task.rootRunning.Value ||
                task.relatedRunning.Value > 0;
            if (task.running.Value != running)
            {
                SetUnknown(view);
                ResolveModel(view, instance.Document, task, true);
                return view;
            }

            bool blockedWithPending = task.terminal != null &&
                String.Equals(task.terminal.kind, "blocked",
                    StringComparison.Ordinal) &&
                (approvals > 0 || questions > 0);
            if (!task.rootRunning.Value && !blockedWithPending &&
                IsRootFailureTerminal(task) &&
                TryTerminal(view, task, settings, nowUtc))
            {
                ResolveModel(view, instance.Document, task, true);
                return view;
            }

            if (approvals > 0 || questions > 0)
            {
                view.State = HaloState.Attention;
                view.Priority = Priority(view.State);
                view.TurnPhase = AgentTurnPhase.AwaitingUser;
                view.AttentionReason = approvals > 0
                    ? AgentAttentionReason.Approval
                    : AgentAttentionReason.UserInput;
                view.Active = true;
                view.StatusDetailKey = "status.deepseek.attention";
            }
            else if (tools > 0)
            {
                view.State = HaloState.Working;
                view.Priority = Priority(view.State);
                view.TurnPhase = AgentTurnPhase.Executing;
                view.Activity = AgentActivityKind.UsingTool;
                view.Active = true;
                view.StatusDetailKey = "status.deepseek.working";
            }
            else if (running)
            {
                view.State = HaloState.Thinking;
                view.Priority = Priority(view.State);
                view.TurnPhase = AgentTurnPhase.Thinking;
                view.Active = true;
                view.StatusDetailKey = "status.deepseek.thinking";
            }
            else if (TryTerminal(view, task, settings, nowUtc))
            {
            }
            else
            {
                view.State = HaloState.Idle;
                view.Priority = Priority(view.State);
                view.TurnPhase = AgentTurnPhase.None;
                view.StatusDetailKey = "status.deepseek.standby";
            }

            ResolveModel(view, instance.Document, task,
                view.StatusDetailKey != "status.deepseek.standby");
            return view;
        }

        private static bool TryTerminal(DeepSeekHarnessTaskView view,
            DeepSeekHarnessTask task, HaloSettings settings,
            DateTime nowUtc)
        {
            DeepSeekHarnessTerminal terminal = task.terminal;
            if (terminal == null)
            {
                return false;
            }
            if (!String.IsNullOrWhiteSpace(task.turnId) &&
                !String.Equals(task.turnId, terminal.turnId,
                    StringComparison.Ordinal))
            {
                return false;
            }
            DateTime endedUtc;
            if (!DeepSeekHarnessSnapshotReader.TryUtc(terminal.endedAtUtc,
                    out endedUtc) || endedUtc > nowUtc.ToUniversalTime()
                    .AddSeconds(2))
            {
                view.LastEventUtc = endedUtc;
                SetUnknown(view);
                return true;
            }

            TimeSpan age = nowUtc.ToUniversalTime() - endedUtc;
            if (String.Equals(terminal.kind, "completed",
                    StringComparison.Ordinal))
            {
                if (age > TimeSpan.FromMinutes(5) ||
                    endedUtc <= settings.GetAcknowledgedUtc(
                        AgentKind.DeepSeekHarness, task.rootSessionId))
                {
                    return false;
                }
                view.State = HaloState.Done;
                view.Priority = Priority(view.State);
                view.TurnPhase = AgentTurnPhase.Completed;
                view.CompletedUtc = endedUtc;
                view.LastEventUtc = endedUtc;
                view.StatusDetailKey = "status.deepseek.done";
                return true;
            }

            bool stopped = String.Equals(terminal.kind, "aborted",
                StringComparison.Ordinal);
            bool failed = String.Equals(terminal.kind, "error",
                    StringComparison.Ordinal) ||
                String.Equals(terminal.kind, "failed",
                    StringComparison.Ordinal) ||
                String.Equals(terminal.kind, "blocked",
                    StringComparison.Ordinal);
            if (!stopped && !failed)
            {
                if (age <= TimeSpan.FromHours(12))
                {
                    view.LastEventUtc = endedUtc;
                    SetUnknown(view);
                    return true;
                }
                return false;
            }
            if (age > TimeSpan.FromHours(12) ||
                endedUtc <= settings.GetAcknowledgedErrorUtc(
                    AgentKind.DeepSeekHarness))
            {
                return false;
            }
            view.State = HaloState.Error;
            view.Priority = Priority(view.State);
            view.TurnPhase = AgentTurnPhase.Failed;
            view.FailureSeverity = AgentFailureSeverity.FatalTurn;
            view.CompletedUtc = DateTime.MinValue;
            view.LastEventUtc = endedUtc;
            view.StatusDetailKey = stopped
                ? "status.deepseek.stopped"
                : String.Equals(terminal.kind, "blocked",
                    StringComparison.Ordinal)
                    ? "status.deepseek.blocked" : "status.deepseek.error";
            view.EvidenceKind = stopped
                ? "deepseek_harness_stopped" :
                    "deepseek_harness_failed";
            return true;
        }

        private static bool IsRootFailureTerminal(DeepSeekHarnessTask task)
        {
            if (task.terminal == null)
            {
                return false;
            }
            string kind = task.terminal.kind;
            return String.Equals(kind, "error", StringComparison.Ordinal) ||
                String.Equals(kind, "failed", StringComparison.Ordinal) ||
                String.Equals(kind, "aborted", StringComparison.Ordinal) ||
                String.Equals(kind, "blocked", StringComparison.Ordinal);
        }

        private static bool CanClassify(
            DeepSeekHarnessSnapshotDocument document,
            DeepSeekHarnessTask task)
        {
            DeepSeekHarnessCapabilities capabilities =
                document.capabilities;
            return document.baselineReady && capabilities != null &&
                capabilities.lifecycle && capabilities.toolName &&
                capabilities.attention && capabilities.subagents &&
                task.relationsResolved == true &&
                task.rootRunning.HasValue &&
                task.relatedRunning.HasValue &&
                task.running.HasValue && task.activeToolCount.HasValue &&
                task.pendingApprovalCount.HasValue &&
                task.blockingQuestionCount.HasValue &&
                task.relatedRunning.Value >= 0 &&
                task.activeToolCount.Value >= 0 &&
                task.pendingApprovalCount.Value >= 0 &&
                task.blockingQuestionCount.Value >= 0;
        }

        private static void ResolveModel(DeepSeekHarnessTaskView view,
            DeepSeekHarnessSnapshotDocument document,
            DeepSeekHarnessTask task, bool preferActual)
        {
            DeepSeekHarnessModel model = null;
            bool currentTurn = false;
            DeepSeekHarnessCapabilities capabilities = document.capabilities;
            if (preferActual && capabilities.actualModel &&
                IsModel(task.mainModel) &&
                String.Equals(task.mainModel.source, "actual-request",
                    StringComparison.Ordinal))
            {
                string expectedTurn = task.turnId;
                if (task.terminal != null && view.State == HaloState.Done ||
                    task.terminal != null && view.State == HaloState.Error)
                {
                    expectedTurn = task.terminal.turnId;
                }
                if (!String.IsNullOrWhiteSpace(expectedTurn) &&
                    String.Equals(task.mainModel.turnId, expectedTurn,
                        StringComparison.Ordinal))
                {
                    model = task.mainModel;
                    currentTurn = true;
                }
            }
            if (model == null && capabilities.nextModel &&
                IsModel(task.nextModel) &&
                String.Equals(task.nextModel.source, "next-selection",
                    StringComparison.Ordinal))
            {
                model = task.nextModel;
            }
            if (model == null && capabilities.nextModel &&
                IsModel(task.mainModel) &&
                String.Equals(task.mainModel.source, "next-selection",
                    StringComparison.Ordinal))
            {
                model = task.mainModel;
            }
            if (model == null)
            {
                view.ModelName = String.Empty;
                view.ModelProvider = String.Empty;
                view.ModelNameKey = "details.deepseek.model.not_fetched";
                view.ModelSourceKey = String.Empty;
                return;
            }

            view.ModelName = !String.IsNullOrWhiteSpace(model.name)
                ? model.name.Trim() : model.modelId;
            view.ModelProvider = model.providerId ?? String.Empty;
            view.ModelNameKey = String.Empty;
            view.ModelSourceKey = currentTurn
                ? "details.deepseek.model_source.current_turn"
                : "details.deepseek.model_source.next_request";
        }

        private static bool IsModel(DeepSeekHarnessModel model)
        {
            return model != null &&
                !String.IsNullOrWhiteSpace(model.providerId) &&
                !String.IsNullOrWhiteSpace(model.modelId);
        }

        private static DateTime ReadActivity(DeepSeekHarnessTask task,
            DateTime nowUtc)
        {
            DateTime activity;
            return DeepSeekHarnessSnapshotReader.TryUtc(
                task.lastActivityUtc, out activity) &&
                activity <= nowUtc.ToUniversalTime().AddSeconds(2)
                ? activity : DateTime.MinValue;
        }

        private static DeepSeekHarnessTaskView SelectTaskAndOrder(
            List<DeepSeekHarnessTaskView> views, string previousRootId)
        {
            if (views.Count == 0)
            {
                return null;
            }
            views.Sort(delegate(DeepSeekHarnessTaskView left,
                DeepSeekHarnessTaskView right)
            {
                int priority = right.Priority.CompareTo(left.Priority);
                if (priority != 0) return priority;
                int activity = right.ActivityUtc.CompareTo(left.ActivityUtc);
                return activity != 0 ? activity : String.Compare(
                    left.Task.rootSessionId, right.Task.rootSessionId,
                    StringComparison.Ordinal);
            });

            DeepSeekHarnessTaskView selected = views[0];
            if (!String.IsNullOrWhiteSpace(previousRootId) &&
                !String.Equals(views[0].Task.rootSessionId,
                    previousRootId, StringComparison.Ordinal))
            {
                for (int index = 1; index < views.Count; index++)
                {
                    DeepSeekHarnessTaskView candidate = views[index];
                    if (candidate.Priority != selected.Priority ||
                        candidate.ActivityUtc != selected.ActivityUtc)
                    {
                        break;
                    }
                    if (String.Equals(candidate.Task.rootSessionId,
                            previousRootId, StringComparison.Ordinal))
                    {
                        selected = candidate;
                        views.RemoveAt(index);
                        views.Insert(0, selected);
                        break;
                    }
                }
            }
            return selected;
        }

        private static SessionSnapshot ToSession(
            DeepSeekHarnessTaskView view)
        {
            DateTime eventUtc = view.LastEventUtc != DateTime.MinValue
                ? view.LastEventUtc : view.ActivityUtc;
            return new SessionSnapshot
            {
                ThreadId = view.Task.rootSessionId,
                TaskTitle = view.Title,
                ProjectName = String.Empty,
                State = view.State,
                Action = L10n.Instance[view.StatusDetailKey],
                LastEventUtc = eventUtc,
                CompletedUtc = view.CompletedUtc,
                Active = view.Active,
                Agent = AgentKind.DeepSeekHarness,
                TurnPhase = view.TurnPhase,
                Activity = view.Activity,
                EvidenceSource = AgentEvidenceSource.DeepSeekHarnessBridge,
                EvidenceKind = view.EvidenceKind,
                EvidenceId = view.Task.turnId ?? String.Empty,
                AttentionReason = view.AttentionReason,
                FailureSeverity = view.FailureSeverity,
                ModelName = view.ModelName ?? String.Empty,
                ModelProvider = view.ModelProvider ?? String.Empty
            };
        }

        private static void SetUnknown(DeepSeekHarnessTaskView view)
        {
            view.State = HaloState.Idle;
            view.Priority = 1;
            view.TurnPhase = AgentTurnPhase.None;
            view.Activity = AgentActivityKind.None;
            view.AttentionReason = AgentAttentionReason.None;
            view.FailureSeverity = AgentFailureSeverity.None;
            view.Active = false;
            view.CompletedUtc = DateTime.MinValue;
            if (view.Task.terminal != null)
            {
                DateTime terminalUtc;
                if (DeepSeekHarnessSnapshotReader.TryUtc(
                        view.Task.terminal.endedAtUtc, out terminalUtc))
                {
                    view.LastEventUtc = terminalUtc;
                }
            }
            view.StatusDetailKey = "status.deepseek.unknown";
            view.EvidenceKind = "deepseek_harness_unknown";
        }

        private static int Priority(HaloState state)
        {
            switch (state)
            {
                case HaloState.Error: return 6;
                case HaloState.Attention: return 5;
                case HaloState.Working: return 4;
                case HaloState.Thinking: return 3;
                case HaloState.Done: return 2;
                default: return 0;
            }
        }

        private static bool IsReady(DeepSeekHarnessReadResult source)
        {
            return source != null &&
                source.IntegrationState == AgentIntegrationState.Healthy &&
                source.Instances.Count > 0 &&
                source.Instances.All(delegate(
                    DeepSeekHarnessInstanceSnapshot instance)
                {
                    DeepSeekHarnessCapabilities capabilities =
                        instance.Document.capabilities;
                return instance.Fresh && instance.Document.baselineReady &&
                        capabilities != null && capabilities.lifecycle &&
                        capabilities.toolName && capabilities.attention &&
                        capabilities.subagents;
                });
        }

        internal static bool SnapshotChanged(AgentProviderSnapshot first,
            AgentProviderSnapshot second)
        {
            if (first == null || second == null || first.Aggregate == null ||
                second.Aggregate == null || first.Details == null ||
                second.Details == null)
            {
                return true;
            }
            AggregateSnapshot a = first.Aggregate;
            AggregateSnapshot b = second.Aggregate;
            AgentDetailsSnapshot x = first.Details;
            AgentDetailsSnapshot y = second.Details;
            if (a.State != b.State || a.Presence != b.Presence ||
                !String.Equals(a.Label, b.Label, StringComparison.Ordinal) ||
                !String.Equals(a.Detail, b.Detail, StringComparison.Ordinal) ||
                !String.Equals(x.TaskTitle, y.TaskTitle,
                    StringComparison.Ordinal) ||
                !String.Equals(x.TaskTitleKey, y.TaskTitleKey,
                    StringComparison.Ordinal) ||
                !String.Equals(x.ModelName, y.ModelName,
                    StringComparison.Ordinal) ||
                !String.Equals(x.ModelNameKey, y.ModelNameKey,
                    StringComparison.Ordinal) ||
                !String.Equals(x.ModelSourceKey, y.ModelSourceKey,
                    StringComparison.Ordinal) ||
                !String.Equals(x.StatusDetailKey, y.StatusDetailKey,
                    StringComparison.Ordinal) ||
                first.Capabilities != second.Capabilities ||
                first.Integration.State != second.Integration.State)
            {
                return true;
            }
            if (first.Sessions.Count != second.Sessions.Count)
            {
                return true;
            }
            for (int i = 0; i < first.Sessions.Count; i++)
            {
                SessionSnapshot left = first.Sessions[i];
                SessionSnapshot right = second.Sessions[i];
                if (!String.Equals(left.ThreadId, right.ThreadId,
                        StringComparison.Ordinal) ||
                    left.State != right.State ||
                    !String.Equals(left.TaskTitle, right.TaskTitle,
                        StringComparison.Ordinal) ||
                    !String.Equals(left.ModelName, right.ModelName,
                        StringComparison.Ordinal) ||
                    left.CompletedUtc != right.CompletedUtc)
                {
                    return true;
                }
            }
            return false;
        }

        private sealed class TaskSource
        {
            public readonly DeepSeekHarnessTask Task;
            public readonly DeepSeekHarnessInstanceSnapshot Instance;

            public TaskSource(DeepSeekHarnessTask task,
                DeepSeekHarnessInstanceSnapshot instance)
            {
                Task = task;
                Instance = instance;
            }
        }
    }
}
