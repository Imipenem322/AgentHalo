using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
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
            try
            {
                using (Process host = Process.GetProcessById(document.hostPid))
                using (Process parent = Process.GetProcessById(
                    document.hostParentPid))
                {
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
                        parentStart);
                }
            }
            catch (ArgumentException)
            {
                return Result(DeepSeekHarnessProcessCheck.Exited,
                    DateTime.MinValue);
            }
            catch (InvalidOperationException)
            {
                return Result(DeepSeekHarnessProcessCheck.Exited,
                    DateTime.MinValue);
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
        }

        private static DeepSeekHarnessProcessValidation Result(
            DeepSeekHarnessProcessCheck check, DateTime parentStartUtc)
        {
            return new DeepSeekHarnessProcessValidation
            {
                Check = check,
                ParentStartUtc = parentStartUtc
            };
        }
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
        public AgentIntegrationState IntegrationState;
        public string StatusDetailKey;
        public DateTime LastEventUtc;
        public bool FilesPresent;
        public bool HadStoppedInstance;
        public bool Unsupported;
        public bool Broken;
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
        private readonly JavaScriptSerializer serializer;
        private readonly Dictionary<string, RevisionState> revisions =
            new Dictionary<string, RevisionState>(
                StringComparer.OrdinalIgnoreCase);
        private bool hasSeenInstance;

        public DeepSeekHarnessSnapshotReader()
            : this(Path.Combine(SettingsStorage.AppDirectory, "integrations",
                "deepseek-harness", "runtime"),
                new DeepSeekHarnessProcessValidator())
        {
        }

        internal DeepSeekHarnessSnapshotReader(string directory,
            IDeepSeekHarnessProcessValidator validator)
        {
            runtimeDirectory = Path.GetFullPath(directory);
            processValidator = validator;
            serializer = new JavaScriptSerializer();
            serializer.MaxJsonLength = MaxSnapshotBytes;
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
                    }
                    else
                    {
                        result.Broken = true;
                    }
                    continue;
                }

                hasSeenInstance = true;
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
                    DeepSeekHarnessProcessValidation cachedValidation =
                        processValidator.Validate(cached.Document);
                    if (cachedValidation != null && cachedValidation.Check ==
                            DeepSeekHarnessProcessCheck.Valid &&
                        (cached.ParentStartUtc == DateTime.MinValue ||
                            Math.Abs((cached.ParentStartUtc -
                                cachedValidation.ParentStartUtc)
                                .TotalSeconds) <= 2))
                    {
                        result.Instances.Add(new
                            DeepSeekHarnessInstanceSnapshot
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
                        revisions.Remove(instanceId);
                        if (cachedValidation != null && cachedValidation.Check ==
                            DeepSeekHarnessProcessCheck.Exited)
                        {
                            result.HadStoppedInstance = true;
                        }
                        else
                        {
                            result.Broken = true;
                        }
                    }
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
                    revisions.Remove(instanceId);
                    if (validation != null && validation.Check ==
                        DeepSeekHarnessProcessCheck.Exited)
                    {
                        result.HadStoppedInstance = true;
                    }
                    else
                    {
                        result.Broken = true;
                    }
                }
            }
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
                !String.Equals(document.dshVersion, "0.2.0-rc.2",
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
            DeepSeekHarnessTaskView selected = SelectTask(views,
                previousRootId);
            selectedRootId = selected == null ? String.Empty :
                selected.Task.rootSessionId;
            OrderTasks(views, selected);

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
                    aggregateLabel = CodexSessionMonitor.StateLabel(
                        selected.State);
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
                    ? "UNKNOWN" : CodexSessionMonitor.StateLabel(
                        HaloState.Idle);
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

        private static void OrderTasks(List<DeepSeekHarnessTaskView> views,
            DeepSeekHarnessTaskView selected)
        {
            views.Sort(delegate(DeepSeekHarnessTaskView left,
                DeepSeekHarnessTaskView right)
            {
                if (Object.ReferenceEquals(left, selected)) return -1;
                if (Object.ReferenceEquals(right, selected)) return 1;
                int priority = right.Priority.CompareTo(left.Priority);
                if (priority != 0) return priority;
                int activity = right.ActivityUtc.CompareTo(left.ActivityUtc);
                return activity != 0 ? activity : String.Compare(
                    left.Task.rootSessionId, right.Task.rootSessionId,
                    StringComparison.Ordinal);
            });
        }

        private static DeepSeekHarnessTaskView SelectTask(
            List<DeepSeekHarnessTaskView> views, string previousRootId)
        {
            if (views.Count == 0)
            {
                return null;
            }
            List<DeepSeekHarnessTaskView> ordered = views.OrderByDescending(
                delegate(DeepSeekHarnessTaskView view)
                {
                    return view.Priority;
                }).ThenByDescending(delegate(DeepSeekHarnessTaskView view)
                {
                    return view.ActivityUtc;
                }).ThenBy(delegate(DeepSeekHarnessTaskView view)
                {
                    return view.Task.rootSessionId;
                }, StringComparer.Ordinal).ToList();
            if (!String.IsNullOrWhiteSpace(previousRootId))
            {
                DeepSeekHarnessTaskView first = ordered[0];
                DeepSeekHarnessTaskView previous = ordered.FirstOrDefault(
                    delegate(DeepSeekHarnessTaskView view)
                    {
                        return String.Equals(view.Task.rootSessionId,
                            previousRootId, StringComparison.Ordinal) &&
                            view.Priority == first.Priority &&
                            view.ActivityUtc == first.ActivityUtc;
                    });
                if (previous != null)
                {
                    return previous;
                }
            }
            return ordered[0];
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
