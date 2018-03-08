﻿// Copyright (c) Microsoft.  All Rights Reserved.  Licensed under the Apache License, Version 2.0.  See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.EditAndContinue;
using Microsoft.CodeAnalysis.Editor.Implementation.EditAndContinue;
using Microsoft.CodeAnalysis.Emit;
using Microsoft.CodeAnalysis.ErrorReporting;
using Microsoft.CodeAnalysis.Internal.Log;
using Microsoft.CodeAnalysis.Notification;
using Microsoft.CodeAnalysis.Text;
using Microsoft.DiaSymReader;
using Microsoft.VisualStudio.ComponentModelHost;
using Microsoft.VisualStudio.Editor;
using Microsoft.VisualStudio.LanguageServices.Implementation.EditAndContinue.Interop;
using Microsoft.VisualStudio.LanguageServices.Implementation.ProjectSystem;
using Microsoft.VisualStudio.LanguageServices.Utilities;
using Roslyn.Utilities;
using ShellInterop = Microsoft.VisualStudio.Shell.Interop;
using VsTextSpan = Microsoft.VisualStudio.TextManager.Interop.TextSpan;
using VsThreading = Microsoft.VisualStudio.Threading;
using Document = Microsoft.CodeAnalysis.Document;
using Microsoft.CodeAnalysis.Debugging;
using Microsoft.VisualStudio.Shell.Interop;
using System.Reflection.PortableExecutable;
using Microsoft.VisualStudio.LanguageServices.EditAndContinue;

namespace Microsoft.VisualStudio.LanguageServices.Implementation.EditAndContinue
{
    internal sealed class VsENCRebuildableProjectImpl
    {
        private readonly AbstractProject _vsProject;

        // number of projects that are in the debug state:
        private static int s_debugStateProjectCount;

        // number of projects that are in the break state:
        private static int s_breakStateProjectCount;

        // projects that entered the break state:
        private static readonly List<KeyValuePair<ProjectId, ProjectReadOnlyReason>> s_breakStateEnteredProjects = new List<KeyValuePair<ProjectId, ProjectReadOnlyReason>>();

        // active statements of projects that entered the break state:
        private static readonly List<VsActiveStatement> s_pendingActiveStatements = new List<VsActiveStatement>();

        private static VsReadOnlyDocumentTracker s_readOnlyDocumentTracker;

        internal static readonly TraceLog log = new TraceLog(2048, "EnC");

        private static Solution s_breakStateEntrySolution;

        private static EncDebuggingSessionInfo s_encDebuggingSessionInfo;

        private readonly IDebuggingWorkspaceService _debuggingService;
        private readonly IEditAndContinueService _encService;
        private readonly IActiveStatementTrackingService _trackingService;
        private readonly EditAndContinueDiagnosticUpdateSource _diagnosticProvider;
        private readonly IDebugEncNotify _debugEncNotify;
        private readonly INotificationService _notifications;
        private readonly IVsEditorAdaptersFactoryService _editorAdaptersFactoryService;
        private readonly IDebuggeeModuleMetadataProvider _moduleMetadataProvider;

        #region Per Project State

        private bool _changesApplied;

        // maps VS Active Statement Id, which is unique within this project, to our id
        private Dictionary<uint, ActiveStatementId> _activeStatementIds;

        private ProjectAnalysisSummary _lastEditSessionSummary = ProjectAnalysisSummary.NoChanges;
        private HashSet<uint> _activeMethods;
        private List<VsExceptionRegion> _exceptionRegions;
        private EmitBaseline _committedBaseline;
        private EmitBaseline _pendingBaseline;
        private Project _projectBeingEmitted;

        private ImmutableArray<DocumentId> _documentsWithEmitError = ImmutableArray<DocumentId>.Empty;

        /// <summary>
        /// Initialized when the project switches to debug state.
        /// <see cref="Guid.Empty"/> if the project has no output file or we can't read the MVID.
        /// </summary>
        private Guid _mvid;

        private Lazy<ISymUnmanagedReader5> _pdbReader;

        #endregion

        private bool IsDebuggable => _mvid != Guid.Empty;

        internal VsENCRebuildableProjectImpl(AbstractProject project)
        {
            Contract.Requires(project != null);

            _vsProject = project;

            _debuggingService = _vsProject.Workspace.Services.GetService<IDebuggingWorkspaceService>();
            _trackingService = _vsProject.Workspace.Services.GetService<IActiveStatementTrackingService>();
            _notifications = _vsProject.Workspace.Services.GetService<INotificationService>();

            _debugEncNotify = (IDebugEncNotify)project.ServiceProvider.GetService(typeof(SVsShellDebugger));

            var componentModel = (IComponentModel)project.ServiceProvider.GetService(typeof(SComponentModel));
            _diagnosticProvider = componentModel.GetService<EditAndContinueDiagnosticUpdateSource>();
            _editorAdaptersFactoryService = componentModel.GetService<IVsEditorAdaptersFactoryService>();
            _moduleMetadataProvider = componentModel.GetService<IDebuggeeModuleMetadataProvider>();
            _encService = _debuggingService.EditAndContinueServiceOpt;

            Contract.Requires(_debugEncNotify != null);
            Contract.Requires(_encService != null);
            Contract.Requires(_trackingService != null);
            Contract.Requires(_diagnosticProvider != null);
            Contract.Requires(_editorAdaptersFactoryService != null);
            Contract.Requires(_moduleMetadataProvider != null);
        }

        // called from an edit filter if an edit of a read-only buffer is attempted:
        internal bool OnEdit(DocumentId documentId)
        {
            if (_encService.IsProjectReadOnly(documentId.ProjectId, out var sessionReason, out var projectReason))
            {
                OnReadOnlyDocumentEditAttempt(documentId, sessionReason, projectReason);
                return true;
            }

            return false;
        }

        private void OnReadOnlyDocumentEditAttempt(
            DocumentId documentId,
            SessionReadOnlyReason sessionReason,
            ProjectReadOnlyReason projectReason)
        {
            if (sessionReason == SessionReadOnlyReason.StoppedAtException)
            {
                _debugEncNotify.NotifyEncEditAttemptedAtInvalidStopState();
                return;
            }

            var visualStudioWorkspace = _vsProject.Workspace as VisualStudioWorkspaceImpl;
            var hostProject = visualStudioWorkspace?.GetHostProject(documentId.ProjectId) as AbstractProject;
            if (hostProject?.EditAndContinueImplOpt?._mvid != Guid.Empty)
            {
                _debugEncNotify.NotifyEncEditDisallowedByProject(hostProject.Hierarchy);
                return;
            }
            
            // NotifyEncEditDisallowedByProject is broken if the project isn't built at the time the debugging starts (debugger bug 877586).
            string message;
            if (sessionReason == SessionReadOnlyReason.Running)
            {
                message = ServicesVSResources.ChangesNotAllowedWhileCodeIsRunning;
            }
            else
            {
                Debug.Assert(sessionReason == SessionReadOnlyReason.None);

                switch (projectReason)
                {
                    case ProjectReadOnlyReason.MetadataNotAvailable:
                        message = ServicesVSResources.ChangesNotAllowedIfProjectWasntBuildWhenDebuggingStarted;
                        break;

                    case ProjectReadOnlyReason.NotLoaded:
                        message = ServicesVSResources.ChangesNotAllowedIFAssemblyHasNotBeenLoaded;
                        break;

                    default:
                        throw ExceptionUtilities.UnexpectedValue(projectReason);
                }
            }

            _notifications.SendNotification(message, title: FeaturesResources.Edit_and_Continue1, severity: NotificationSeverity.Error);
        }

        /// <summary>
        /// Since we can't await asynchronous operations we need to wait for them to complete.
        /// The default SynchronizationContext.Wait pumps messages giving the debugger a chance to 
        /// reenter our EnC implementation. To avoid that we use a specialized SynchronizationContext
        /// that doesn't pump messages. We need to make sure though that the async methods we wait for
        /// don't dispatch to foreground thread, otherwise we would end up in a deadlock.
        /// </summary>
        private static VsThreading.SpecializedSyncContext NonReentrantContext
        {
            get
            {
                return VsThreading.ThreadingTools.Apply(VsThreading.NoMessagePumpSyncContext.Default);
            }
        }

        public bool HasCustomMetadataEmitter()
        {
            return true;
        }

        /// <summary>
        /// Invoked when the debugger transitions from Design mode to Run mode or Break mode.
        /// </summary>
        public int StartDebuggingPE()
        {
            try
            {
                log.Write("Enter Debug Mode: project '{0}'", _vsProject.DisplayName);

                // EnC service is global (per solution), but the debugger calls this for each project.
                // Avoid starting the debug session if it has already been started.
                if (_encService.DebuggingSession == null)
                {
                    Debug.Assert(s_debugStateProjectCount == 0);
                    Debug.Assert(s_breakStateProjectCount == 0);
                    Debug.Assert(s_breakStateEnteredProjects.Count == 0);

                    _debuggingService.OnBeforeDebuggingStateChanged(DebuggingState.Design, DebuggingState.Run);

                    _encService.StartDebuggingSession(_vsProject.Workspace.CurrentSolution);
                    s_encDebuggingSessionInfo = new EncDebuggingSessionInfo();

                    s_readOnlyDocumentTracker = new VsReadOnlyDocumentTracker(_encService, _editorAdaptersFactoryService, _vsProject);
                }

                string outputPath = _vsProject.ObjOutputPath;

                // The project doesn't produce a debuggable binary or we can't read it.
                // Continue on since the debugger ignores HResults and we need to handle subsequent calls.
                if (outputPath != null)
                {
                    try
                    {
                        InjectFault_MvidRead();
                        _mvid = ReadMvid(outputPath);
                    }
                    catch (Exception e) when (e is FileNotFoundException || e is DirectoryNotFoundException)
                    {
                        // If the project isn't referenced by the project being debugged it might not be built.
                        // In that case EnC is never allowed for the project, and thus we can assume the project hasn't entered debug state.
                        log.Write("StartDebuggingPE: '{0}' metadata file not found: '{1}'", _vsProject.DisplayName, outputPath);
                        _mvid = Guid.Empty;
                    }
                    catch (Exception e)
                    {
                        log.Write("StartDebuggingPE: error reading MVID of '{0}' ('{1}'): {2}", _vsProject.DisplayName, outputPath, e.Message);
                        _mvid = Guid.Empty;

                        var descriptor = new DiagnosticDescriptor(
                            "ENC0002", 
                            new LocalizableResourceString(nameof(ServicesVSResources.ErrorReadingFile), ServicesVSResources.ResourceManager, typeof(ServicesVSResources)),
                            ServicesVSResources.Error_while_reading_0_colon_1,
                            DiagnosticCategory.EditAndContinue,
                            DiagnosticSeverity.Error, 
                            isEnabledByDefault: true, 
                            customTags: DiagnosticCustomTags.EditAndContinue);

                        _diagnosticProvider.ReportDiagnostics(
                            new EncErrorId(_encService.DebuggingSession, EditAndContinueDiagnosticUpdateSource.DebuggerErrorId),
                            _encService.DebuggingSession.InitialSolution,
                            _vsProject.Id,
                            new[] { Diagnostic.Create(descriptor, Location.None, outputPath, e.Message) });
                    }
                }
                else
                {
                    log.Write("StartDebuggingPE: project has no output path '{0}'", _vsProject.DisplayName);
                    _mvid = Guid.Empty;
                }

                if (_mvid != Guid.Empty)
                {
                    // The debugger doesn't call EnterBreakStateOnPE for projects that don't have MVID.
                    // However a project that's initially not loaded (but it might be in future) enters 
                    // both the debug and break states.
                    s_debugStateProjectCount++;
                }

                _activeMethods = new HashSet<uint>();
                _exceptionRegions = new List<VsExceptionRegion>();
                _activeStatementIds = new Dictionary<uint, ActiveStatementId>();

                // The HResult is ignored by the debugger.
                return VSConstants.S_OK;
            }
            catch (Exception e) when (FatalError.ReportWithoutCrash(e))
            {
                return VSConstants.E_FAIL;
            }
        }

        /// <summary>
        /// Given a path to an assembly, returns its MVID (Module Version ID).
        /// May throw.
        /// </summary>
        /// <exception cref="IOException">If the file at <paramref name="filePath"/> does not exist or cannot be accessed.</exception>
        /// <exception cref="BadImageFormatException">If the file is not an assembly or is somehow corrupted.</exception>
        private static Guid ReadMvid(string filePath)
        {
            Debug.Assert(filePath != null);
            Debug.Assert(PathUtilities.IsAbsolute(filePath));

            using (var reader = new PEReader(FileUtilities.OpenRead(filePath)))
            {
                var metadataReader = reader.GetMetadataReader();
                var mvidHandle = metadataReader.GetModuleDefinition().Mvid;
                var fileMvid = metadataReader.GetGuid(mvidHandle);

                return fileMvid;
            }
        }

        public int StopDebuggingPE()
        {
            try
            {
                log.Write("Exit Debug Mode: project '{0}'", _vsProject.DisplayName);
                Debug.Assert(s_breakStateEnteredProjects.Count == 0);

                // Clear the solution stored while projects were entering break mode. 
                // It should be cleared as soon as all tracked projects enter the break mode 
                // but if the entering break mode fails for some projects we should avoid leaking the solution.
                Debug.Assert(s_breakStateEntrySolution == null);
                s_breakStateEntrySolution = null;

                // EnC service is global (per solution), but the debugger calls this for each project.
                // Avoid ending the debug session if it has already been ended.
                if (_encService.DebuggingSession != null)
                {
                    _debuggingService.OnBeforeDebuggingStateChanged(DebuggingState.Run, DebuggingState.Design);

                    _encService.EndDebuggingSession();
                    LogEncSession();

                    s_encDebuggingSessionInfo = null;
                    s_readOnlyDocumentTracker.Dispose();
                    s_readOnlyDocumentTracker = null;
                }

                if (_mvid != Guid.Empty)
                {
                    _mvid = Guid.Empty;
                    s_debugStateProjectCount--;
                }
                else
                {
                    // an error might have been reported:
                    var errorId = new EncErrorId(_encService.DebuggingSession, EditAndContinueDiagnosticUpdateSource.DebuggerErrorId);
                    _diagnosticProvider.ClearDiagnostics(errorId, _vsProject.Workspace.CurrentSolution, _vsProject.Id, documentIdOpt: null);
                }

                _activeMethods = null;
                _exceptionRegions = null;
                _committedBaseline = null;
                _activeStatementIds = null;
                _projectBeingEmitted = null;

                var pdbReader = Interlocked.Exchange(ref _pdbReader, null);
                if (pdbReader?.IsValueCreated == true)
                {
                    var symReader = pdbReader.Value;
                    if (Marshal.IsComObject(symReader))
                    {
                        Marshal.ReleaseComObject(symReader);
                    }
                }

                // The HResult is ignored by the debugger.
                return VSConstants.S_OK;
            }
            catch (Exception e) when (FatalError.ReportWithoutCrash(e))
            {
                return VSConstants.E_FAIL;
            }
        }

        private static void LogEncSession()
        {
            var sessionId = DebugLogMessage.GetNextId();
            Logger.Log(FunctionId.Debugging_EncSession, DebugLogMessage.Create(sessionId, s_encDebuggingSessionInfo));

            foreach (var editSession in s_encDebuggingSessionInfo.EditSessions)
            {
                var editSessionId = DebugLogMessage.GetNextId();
                Logger.Log(FunctionId.Debugging_EncSession_EditSession, DebugLogMessage.Create(sessionId, editSessionId, editSession));

                if (editSession.EmitDeltaErrorIds != null)
                {
                    foreach (var error in editSession.EmitDeltaErrorIds)
                    {
                        Logger.Log(FunctionId.Debugging_EncSession_EditSession_EmitDeltaErrorId, DebugLogMessage.Create(sessionId, editSessionId, error));
                    }
                }

                foreach (var rudeEdit in editSession.RudeEdits)
                {
                    Logger.Log(FunctionId.Debugging_EncSession_EditSession_RudeEdit, DebugLogMessage.Create(sessionId, editSessionId, rudeEdit, blocking: editSession.HadRudeEdits));
                }
            }
        }

        /// <summary>
        /// Get MVID and file name of the project's output file.
        /// </summary>
        /// <remarks>
        /// The MVID is used by the debugger to identify modules loaded into debuggee that correspond to this project.
        /// The path seems to be unused.
        /// 
        /// The output file path might be different from the path of the module loaded into the process.
        /// For example, the binary produced by the C# compiler is stores in obj directory, 
        /// and then copied to bin directory from which it is loaded to the debuggee.
        /// 
        /// The binary produced by the compiler can also be rewritten by post-processing tools.
        /// The debugger assumes that the MVID of the compiler's output file at the time we start debugging session 
        /// is the same as the MVID of the module loaded into debuggee. The original MVID might be different though.
        /// </remarks>
        public int GetPEidentity(Guid[] pMVID, string[] pbstrPEName)
        {
            Debug.Assert(_encService.DebuggingSession != null);

            if (_mvid == Guid.Empty)
            {
                return VSConstants.E_FAIL;
            }

            if (pMVID != null && pMVID.Length != 0)
            {
                pMVID[0] = _mvid;
            }

            if (pbstrPEName != null && pbstrPEName.Length != 0)
            {
                var outputPath = _vsProject.ObjOutputPath;
                Debug.Assert(outputPath != null);

                pbstrPEName[0] = Path.GetFileName(outputPath);
            }

            return VSConstants.S_OK;
        }

        /// <summary>
        /// Called by the debugger when entering a Break state. 
        /// </summary>
        /// <param name="encBreakReason">Reason for transition to Break state.</param>
        /// <param name="pActiveStatements">Statements active when the debuggee is stopped.</param>
        /// <param name="cActiveStatements">Length of <paramref name="pActiveStatements"/>.</param>
        public int EnterBreakStateOnPE(Interop.ENC_BREAKSTATE_REASON encBreakReason, ShellInterop.ENC_ACTIVE_STATEMENT[] pActiveStatements, uint cActiveStatements)
        {
            try
            {
                using (NonReentrantContext)
                {
                    log.Write("Enter {2}Break Mode: project '{0}', AS#: {1}", _vsProject.DisplayName, pActiveStatements != null ? pActiveStatements.Length : -1, encBreakReason == ENC_BREAKSTATE_REASON.ENC_BREAK_EXCEPTION ? "Exception " : "");

                    Debug.Assert(cActiveStatements == (pActiveStatements != null ? pActiveStatements.Length : 0));
                    Debug.Assert(s_breakStateProjectCount < s_debugStateProjectCount);
                    Debug.Assert(s_breakStateProjectCount > 0 || _exceptionRegions.Count == 0);
                    Debug.Assert(s_breakStateProjectCount == s_breakStateEnteredProjects.Count);
                    Debug.Assert(IsDebuggable);

                    if (s_breakStateEntrySolution == null)
                    {
                        _debuggingService.OnBeforeDebuggingStateChanged(DebuggingState.Run, DebuggingState.Break);

                        s_breakStateEntrySolution = _vsProject.Workspace.CurrentSolution;

                        // TODO: This is a workaround for a debugger bug in which not all projects exit the break state.
                        // Reset the project count.
                        s_breakStateProjectCount = 0;
                    }

                    ProjectReadOnlyReason state;
                    if (pActiveStatements != null)
                    {
                        AddActiveStatements(s_breakStateEntrySolution, pActiveStatements);
                        state = ProjectReadOnlyReason.None;
                    }
                    else
                    {
                        // unfortunately the debugger doesn't provide details:
                        state = ProjectReadOnlyReason.NotLoaded;
                    }

                    // If pActiveStatements is null the EnC Manager failed to retrieve the module corresponding 
                    // to the project in the debuggee. We won't include such projects in the edit session.
                    s_breakStateEnteredProjects.Add(KeyValuePair.Create(_vsProject.Id, state));
                    s_breakStateProjectCount++;

                    // EnC service is global, but the debugger calls this for each project.
                    // Avoid starting the edit session until all projects enter break state.
                    if (s_breakStateEnteredProjects.Count == s_debugStateProjectCount)
                    {
                        Debug.Assert(_encService.EditSession == null);
                        Debug.Assert(s_pendingActiveStatements.TrueForAll(s => s.Owner._activeStatementIds.Count == 0));

                        var byDocument = new Dictionary<DocumentId, ImmutableArray<ActiveStatementSpan>>();

                        // note: fills in activeStatementIds of projects that own the active statements:
                        GroupActiveStatements(s_pendingActiveStatements, byDocument);

                        // When stopped at exception: All documents are read-only, but the files might be changed outside of VS.
                        // So we start an edit session as usual and report a rude edit for all changes we see.
                        bool stoppedAtException = encBreakReason == ENC_BREAKSTATE_REASON.ENC_BREAK_EXCEPTION;

                        var projectStates = ImmutableDictionary.CreateRange(s_breakStateEnteredProjects);

                        _encService.StartEditSession(s_breakStateEntrySolution, byDocument, projectStates, stoppedAtException);
                        _trackingService.StartTracking(_encService.EditSession);

                        s_readOnlyDocumentTracker.UpdateWorkspaceDocuments();

                        // When tracking is started the tagger is notified and the active statements are highlighted.
                        // Add the handler that notifies the debugger *after* that initial tagger notification,
                        // so that it's not triggered unless an actual change in leaf AS occurs.
                        _trackingService.TrackingSpansChanged += TrackingSpansChanged;
                    }
                }

                // The debugger ignores the result.
                return VSConstants.S_OK;
            }
            catch (Exception e) when (FatalError.ReportWithoutCrash(e))
            {
                return VSConstants.E_FAIL;
            }
            finally
            {
                // TODO: This is a workaround for a debugger bug.
                // Ensure that the state gets reset even if if `GroupActiveStatements` throws an exception.
                if (s_breakStateEnteredProjects.Count == s_debugStateProjectCount)
                {
                    // we don't need these anymore:
                    s_pendingActiveStatements.Clear();
                    s_breakStateEnteredProjects.Clear();
                    s_breakStateEntrySolution = null;
                }
            }
        }

        private void TrackingSpansChanged(bool leafChanged)
        {
            //log.Write("Tracking spans changed: {0}", leafChanged);

            //if (leafChanged)
            //{
            //    // fire and forget:
            //    Application.Current.Dispatcher.InvokeAsync(() =>
            //    {
            //        log.Write("Notifying debugger of active statement change.");
            //        var debugNotify = (IDebugEncNotify)_vsProject.ServiceProvider.GetService(typeof(ShellInterop.SVsShellDebugger));
            //        debugNotify.NotifyEncUpdateCurrentStatement();
            //    });
            //}
        }

        private struct VsActiveStatement
        {
            public readonly DocumentId DocumentId;
            public readonly uint StatementId;
            public readonly ActiveStatementSpan Span;
            public readonly VsENCRebuildableProjectImpl Owner;

            public VsActiveStatement(VsENCRebuildableProjectImpl owner, uint statementId, DocumentId documentId, ActiveStatementSpan span)
            {
                this.Owner = owner;
                this.StatementId = statementId;
                this.DocumentId = documentId;
                this.Span = span;
            }
        }

        private struct VsExceptionRegion
        {
            public readonly uint ActiveStatementId;
            public readonly int Ordinal;
            public readonly uint MethodToken;
            public readonly LinePositionSpan Span;

            public VsExceptionRegion(uint activeStatementId, int ordinal, uint methodToken, LinePositionSpan span)
            {
                this.ActiveStatementId = activeStatementId;
                this.Span = span;
                this.MethodToken = methodToken;
                this.Ordinal = ordinal;
            }
        }

        // See InternalApis\vsl\inc\encbuild.idl
        private const int TEXT_POSITION_ACTIVE_STATEMENT = 1;

        private void AddActiveStatements(Solution solution, ShellInterop.ENC_ACTIVE_STATEMENT[] vsActiveStatements)
        {
            Debug.Assert(_activeMethods.Count == 0);
            Debug.Assert(_exceptionRegions.Count == 0);

            foreach (var vsActiveStatement in vsActiveStatements)
            {
                log.DebugWrite("+AS[{0}]: {1} {2} {3} {4} '{5}'",
                    unchecked((int)vsActiveStatement.id),
                    vsActiveStatement.tsPosition.iStartLine,
                    vsActiveStatement.tsPosition.iStartIndex,
                    vsActiveStatement.tsPosition.iEndLine,
                    vsActiveStatement.tsPosition.iEndIndex,
                    vsActiveStatement.filename);

                // TODO (tomat):
                // Active statement is in user hidden code. The only information that we have from the debugger
                // is the method token. We don't need to track the statement (it's not in user code anyways),
                // but we should probably track the list of such methods in order to preserve their local variables.
                // Not sure what's exactly the scenario here, perhaps modifying async method/iterator? 
                // Dev12 just ignores these.
                if (vsActiveStatement.posType != TEXT_POSITION_ACTIVE_STATEMENT)
                {
                    continue;
                }

                var flags = (ActiveStatementFlags)vsActiveStatement.ASINFO;

                // Finds a document id in the solution with the specified file path.
                DocumentId documentId = solution.GetDocumentIdsWithFilePath(vsActiveStatement.filename)
                    .Where(dId => dId.ProjectId == _vsProject.Id).SingleOrDefault();

                if (documentId != null)
                {
                    var document = solution.GetDocument(documentId);
                    Debug.Assert(document != null);

                    SourceText source = document.GetTextAsync(default).Result;
                    LinePositionSpan lineSpan = vsActiveStatement.tsPosition.ToLinePositionSpan();

                    // If the PDB is out of sync with the source we might get bad spans.
                    var sourceLines = source.Lines;
                    if (lineSpan.End.Line >= sourceLines.Count || sourceLines.GetPosition(lineSpan.End) > sourceLines[sourceLines.Count - 1].EndIncludingLineBreak)
                    {
                        log.Write("AS out of bounds (line count is {0})", source.Lines.Count);
                        continue;
                    }

                    SyntaxNode syntaxRoot = document.GetSyntaxRootAsync(default).Result;

                    var analyzer = document.Project.LanguageServices.GetService<IEditAndContinueAnalyzer>();

                    s_pendingActiveStatements.Add(new VsActiveStatement(
                        this,
                        vsActiveStatement.id,
                        document.Id,
                        new ActiveStatementSpan(flags, lineSpan)));

                    bool isLeaf = (flags & ActiveStatementFlags.LeafFrame) != 0;
                    var ehRegions = analyzer.GetExceptionRegions(source, syntaxRoot, lineSpan, isLeaf);

                    for (int i = 0; i < ehRegions.Length; i++)
                    {
                        _exceptionRegions.Add(new VsExceptionRegion(
                            vsActiveStatement.id,
                            i,
                            vsActiveStatement.methodToken,
                            ehRegions[i]));
                    }
                }

                _activeMethods.Add(vsActiveStatement.methodToken);
            }
        }

        private static void GroupActiveStatements(
            IEnumerable<VsActiveStatement> activeStatements,
            Dictionary<DocumentId, ImmutableArray<ActiveStatementSpan>> byDocument)
        {
            var spans = new List<ActiveStatementSpan>();

            foreach (var grouping in activeStatements.GroupBy(s => s.DocumentId))
            {
                var documentId = grouping.Key;

                foreach (var activeStatement in grouping.OrderBy(s => s.Span.Span.Start))
                {
                    int ordinal = spans.Count;

                    // register vsid with the project that owns the active statement:
                    activeStatement.Owner._activeStatementIds.Add(activeStatement.StatementId, new ActiveStatementId(documentId, ordinal));

                    spans.Add(activeStatement.Span);
                }

                byDocument.Add(documentId, spans.AsImmutable());
                spans.Clear();
            }
        }

        /// <summary>
        /// Returns the number of exception regions around current active statements.
        /// This is called when the project is entering a break right after 
        /// <see cref="EnterBreakStateOnPE"/> and prior to <see cref="GetExceptionSpans"/>.
        /// </summary>
        /// <remarks>
        /// Called by EnC manager.
        /// </remarks>
        public int GetExceptionSpanCount(out uint pcExceptionSpan)
        {
            pcExceptionSpan = (uint)_exceptionRegions.Count;
            return VSConstants.S_OK;
        }

        /// <summary>
        /// Returns information about exception handlers in the source.
        /// </summary>
        /// <remarks>
        /// Called by EnC manager.
        /// </remarks>
        public int GetExceptionSpans(uint celt, ShellInterop.ENC_EXCEPTION_SPAN[] rgelt, ref uint pceltFetched)
        {
            Debug.Assert(celt == rgelt.Length);
            Debug.Assert(celt == _exceptionRegions.Count);

            for (int i = 0; i < _exceptionRegions.Count; i++)
            {
                rgelt[i] = new ShellInterop.ENC_EXCEPTION_SPAN()
                {
                    id = (uint)i,
                    methodToken = _exceptionRegions[i].MethodToken,
                    tsPosition = _exceptionRegions[i].Span.ToVsTextSpan()
                };
            }

            pceltFetched = celt;
            return VSConstants.S_OK;
        }

        /// <summary>
        /// Called by the debugger whenever it needs to determine a position of an active statement.
        /// E.g. the user clicks on a frame in a call stack.
        /// </summary>
        /// <remarks>
        /// Called when applying change, when setting current IP, a notification is received from 
        /// <see cref="IDebugEncNotify.NotifyEncUpdateCurrentStatement"/>, etc.
        /// In addition this API is exposed on IDebugENC2 COM interface so it can be used anytime by other components.
        /// </remarks>
        public int GetCurrentActiveStatementPosition(uint vsId, VsTextSpan[] ptsNewPosition)
        {
            try
            {
                using (NonReentrantContext)
                {
                    Debug.Assert(IsDebuggable);

                    var session = _encService.EditSession;
                    var ids = _activeStatementIds;
                    // Can be called anytime, even outside of an edit/debug session.
                    // We might not have an active statement available if PDB got out of sync with the source.
                    if (session == null || ids == null || !ids.TryGetValue(vsId, out var id))
                    {
                        log.Write("GetCurrentActiveStatementPosition failed for AS {0}.", unchecked((int)vsId));
                        return VSConstants.E_FAIL;
                    }

                    Document document = _vsProject.Workspace.CurrentSolution.GetDocument(id.DocumentId);
                    SourceText text = document.GetTextAsync(default).Result;
                    LinePositionSpan lineSpan;
                    // Try to get spans from the tracking service first.
                    // We might get an imprecise result if the document analysis hasn't been finished yet and 
                    // the active statement has structurally changed, but that's ok. The user won't see an updated tag
                    // for the statement until the analysis finishes anyways.
                    if (_trackingService.TryGetSpan(id, text, out var span) && span.Length > 0)
                    {
                        lineSpan = text.Lines.GetLinePositionSpan(span);
                    }
                    else
                    {
                        var activeSpans = session.GetDocumentAnalysis(document).GetValue(default).ActiveStatements;
                        if (activeSpans.IsDefault)
                        {
                            // The document has syntax errors and the tracking span is gone.
                            log.Write("Position not available for AS {0} due to syntax errors", unchecked((int)vsId));
                            return VSConstants.E_FAIL;
                        }

                        lineSpan = activeSpans[id.Ordinal];
                    }

                    ptsNewPosition[0] = lineSpan.ToVsTextSpan();
                    log.DebugWrite("AS position: {0} ({1},{2})-({3},{4}) {5}", 
                        unchecked((int)vsId), 
                        lineSpan.Start.Line, lineSpan.Start.Character, lineSpan.End.Line, lineSpan.End.Character,
                        (int)session.BaseActiveStatements[id.DocumentId][id.Ordinal].Flags);

                    return VSConstants.S_OK;
                }
            }
            catch (Exception e) when (FatalError.ReportWithoutCrash(e))
            {
                return VSConstants.E_FAIL;
            }
        }

        /// <summary>
        /// Returns the state of the changes made to the source. 
        /// The EnC manager calls this to determine whether there are any changes to the source 
        /// and if so whether there are any rude edits.
        /// </summary>
        public int GetENCBuildState(ShellInterop.ENC_BUILD_STATE[] pENCBuildState)
        {
            try
            {
                using (NonReentrantContext)
                {
                    Debug.Assert(pENCBuildState != null && pENCBuildState.Length == 1);

                    // GetENCBuildState is called outside of edit session (at least) in following cases:
                    // 1) when the debugger is determining whether a source file checksum matches the one in PDB.
                    // 2) when the debugger is setting the next statement and a change is pending
                    //    See CDebugger::SetNextStatement(CTextPos* pTextPos, bool WarnOnFunctionChange):
                    // 
                    //    pENC2->ExitBreakState();
                    //    >>> hr = GetCodeContextOfPosition(pTextPos, &pCodeContext, &pProgram, true, true);
                    //    pENC2->EnterBreakState(m_pSession, GetEncBreakReason());
                    //
                    // The debugger seem to expect ENC_NOT_MODIFIED in these cases, otherwise errors occur.

                    if (_changesApplied || _encService.EditSession == null)
                    {
                        _lastEditSessionSummary = ProjectAnalysisSummary.NoChanges;
                    }
                    else
                    {
                        // Fetch the latest snapshot of the project and get an analysis summary for any changes 
                        // made since the break mode was entered.
                        var currentProject = _vsProject.Workspace.CurrentSolution.GetProject(_vsProject.Id);
                        if (currentProject == null)
                        {
                            // If the project has yet to be loaded into the solution (which may be the case,
                            // since they are loaded on-demand), then it stands to reason that it has not yet
                            // been modified.
                            // TODO (https://github.com/dotnet/roslyn/issues/1204): this check should be unnecessary.
                            _lastEditSessionSummary = ProjectAnalysisSummary.NoChanges;
                            log.Write("Project '{0}' has not yet been loaded into the solution", _vsProject.DisplayName);
                        }
                        else
                        {
                            _projectBeingEmitted = currentProject;
                            _lastEditSessionSummary = GetProjectAnalysisSummary(_projectBeingEmitted);
                        }

                        _encService.EditSession.LogBuildState(_lastEditSessionSummary);
                    }

                    switch (_lastEditSessionSummary)
                    {
                        case ProjectAnalysisSummary.NoChanges:
                            pENCBuildState[0] = ShellInterop.ENC_BUILD_STATE.ENC_NOT_MODIFIED;
                            break;

                        case ProjectAnalysisSummary.CompilationErrors:
                            pENCBuildState[0] = ShellInterop.ENC_BUILD_STATE.ENC_COMPILE_ERRORS;
                            break;

                        case ProjectAnalysisSummary.RudeEdits:
                            pENCBuildState[0] = ShellInterop.ENC_BUILD_STATE.ENC_NONCONTINUABLE_ERRORS;
                            break;

                        case ProjectAnalysisSummary.ValidChanges:
                        case ProjectAnalysisSummary.ValidInsignificantChanges:
                            // The debugger doesn't distinguish between these two.
                            pENCBuildState[0] = ShellInterop.ENC_BUILD_STATE.ENC_APPLY_READY;
                            break;

                        default:
                            throw ExceptionUtilities.UnexpectedValue(_lastEditSessionSummary);
                    }

                    log.Write("EnC state of '{0}' queried: {1}{2}",
                        _vsProject.DisplayName,
                        EncStateToString(pENCBuildState[0]),
                        _encService.EditSession != null ? "" : " (no session)");

                    return VSConstants.S_OK;
                }
            }
            catch (Exception e) when (FatalError.ReportWithoutCrash(e))
            {
                return VSConstants.E_FAIL;
            }
        }

        private static string EncStateToString(ENC_BUILD_STATE state)
        {
            switch (state)
            {
                case ENC_BUILD_STATE.ENC_NOT_MODIFIED: return "ENC_NOT_MODIFIED";
                case ENC_BUILD_STATE.ENC_NONCONTINUABLE_ERRORS: return "ENC_NONCONTINUABLE_ERRORS";
                case ENC_BUILD_STATE.ENC_COMPILE_ERRORS: return "ENC_COMPILE_ERRORS";
                case ENC_BUILD_STATE.ENC_APPLY_READY: return "ENC_APPLY_READY";
                default: return state.ToString();
            }
        }

        private ProjectAnalysisSummary GetProjectAnalysisSummary(Project project)
        {
            if (!IsDebuggable)
            {
                return ProjectAnalysisSummary.NoChanges;
            }

            var cancellationToken = default(CancellationToken);
            return _encService.EditSession.GetProjectAnalysisSummaryAsync(project, cancellationToken).Result;
        }

        public int ExitBreakStateOnPE()
        {
            try
            {
                using (NonReentrantContext)
                {
                    // The debugger calls Exit without previously calling Enter if the project's MVID isn't available.
                    if (!IsDebuggable)
                    {
                        return VSConstants.S_OK;
                    }

                    log.Write("Exit Break Mode: project '{0}'", _vsProject.DisplayName);

                    // EnC service is global, but the debugger calls this for each project.
                    // Avoid ending the edit session if it has already been ended.
                    if (_encService.EditSession != null)
                    {
                        Debug.Assert(s_breakStateProjectCount == s_debugStateProjectCount);

                        _debuggingService.OnBeforeDebuggingStateChanged(DebuggingState.Break, DebuggingState.Run);

                        _encService.EditSession.LogEditSession(s_encDebuggingSessionInfo);
                        _encService.EndEditSession();
                        _trackingService.EndTracking();

                        s_readOnlyDocumentTracker.UpdateWorkspaceDocuments();

                        _trackingService.TrackingSpansChanged -= TrackingSpansChanged;
                    }

                    _exceptionRegions.Clear();
                    _activeMethods.Clear();
                    _activeStatementIds.Clear();

                    s_breakStateProjectCount--;
                    Debug.Assert(s_breakStateProjectCount >= 0);

                    _changesApplied = false;

                    _diagnosticProvider.ClearDiagnostics(
                        new EncErrorId(_encService.DebuggingSession, EditAndContinueDiagnosticUpdateSource.EmitErrorId), 
                        _vsProject.Workspace.CurrentSolution,
                        _vsProject.Id, 
                        _documentsWithEmitError);

                    _documentsWithEmitError = ImmutableArray<DocumentId>.Empty;
                }

                // HResult ignored by the debugger
                return VSConstants.S_OK;
            }
            catch (Exception e) when (FatalError.ReportWithoutCrash(e))
            {
                return VSConstants.E_FAIL;
            }
        }

        public unsafe int BuildForEnc(object pUpdatePE)
        {
            try
            {
                log.Write("Applying changes to {0}", _vsProject.DisplayName);

                Debug.Assert(_encService.EditSession != null);
                Debug.Assert(!_encService.EditSession.StoppedAtException);

                // Non-debuggable project has no changes.
                Debug.Assert(IsDebuggable);

                if (_changesApplied)
                {
                    log.Write("Changes already applied to {0}, can't apply again", _vsProject.DisplayName);
                    throw ExceptionUtilities.Unreachable;
                }

                // The debugger always calls GetENCBuildState right before BuildForEnc.
                Debug.Assert(_projectBeingEmitted != null);
                Debug.Assert(_lastEditSessionSummary == GetProjectAnalysisSummary(_projectBeingEmitted));

                // The debugger should have called GetENCBuildState before calling BuildForEnc.
                // Unfortunately, there is no way how to tell the debugger that the changes were not significant,
                // so we'll to emit an empty delta. See bug 839558.
                Debug.Assert(_lastEditSessionSummary == ProjectAnalysisSummary.ValidInsignificantChanges ||
                             _lastEditSessionSummary == ProjectAnalysisSummary.ValidChanges);

                var updater = (IDebugUpdateInMemoryPE2)pUpdatePE;

                if (_committedBaseline == null)
                {
                    var previousPdbReader = Interlocked.Exchange(ref _pdbReader, MarshalPdbReader(updater));

                    // PDB reader should have been nulled out when debugging stopped:
                    Contract.ThrowIfFalse(previousPdbReader == null);
                }

                // ISymUnmanagedReader can only be accessed from an MTA thread,
                // so dispatch emit to one of thread pool threads, which are MTA.
                var emitTask = Task.Factory.SafeStartNew(EmitProjectDelta, CancellationToken.None, TaskScheduler.Default);

                Deltas delta;
                using (NonReentrantContext)
                {
                    delta = emitTask.Result;

                    if (delta == null)
                    {
                        // A diagnostic or non-fatal Watson has already been reported by the emit task
                        return VSConstants.E_FAIL;
                    }
                }

                var errorId = new EncErrorId(_encService.DebuggingSession, EditAndContinueDiagnosticUpdateSource.EmitErrorId);

                // Clear diagnostics, in case the project was built before and failed due to errors.
                _diagnosticProvider.ClearDiagnostics(errorId, _projectBeingEmitted.Solution, _vsProject.Id, _documentsWithEmitError);

                if (!delta.EmitResult.Success)
                {
                    var errors = delta.EmitResult.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error);
                    _documentsWithEmitError = _diagnosticProvider.ReportDiagnostics(errorId, _projectBeingEmitted.Solution, _vsProject.Id, errors);
                    _encService.EditSession.LogEmitProjectDeltaErrors(errors.Select(e => e.Id));

                    return VSConstants.E_FAIL;
                }

                _documentsWithEmitError = ImmutableArray<DocumentId>.Empty;
                SetFileUpdates(updater, delta.LineEdits);

                updater.SetDeltaIL(delta.IL.Value, (uint)delta.IL.Value.Length);
                updater.SetDeltaPdb(SymUnmanagedStreamFactory.CreateStream(delta.Pdb.Stream));
                updater.SetRemapMethods(delta.Pdb.UpdatedMethods, (uint)delta.Pdb.UpdatedMethods.Length);
                updater.SetDeltaMetadata(delta.Metadata.Bytes, (uint)delta.Metadata.Bytes.Length);

                _pendingBaseline = delta.EmitResult.Baseline;

#if DEBUG
                fixed (byte* deltaMetadataPtr = &delta.Metadata.Bytes[0])
                {
                    var reader = new System.Reflection.Metadata.MetadataReader(deltaMetadataPtr, delta.Metadata.Bytes.Length);
                    var moduleDef = reader.GetModuleDefinition();

                    log.DebugWrite("Gen {0}: MVID={1}, BaseId={2}, EncId={3}",
                        moduleDef.Generation,
                        reader.GetGuid(moduleDef.Mvid).ToString(),
                        reader.GetGuid(moduleDef.BaseGenerationId).ToString(),
                        reader.GetGuid(moduleDef.GenerationId).ToString());
                }
#endif

                return VSConstants.S_OK;
            }
            catch (Exception e) when (FatalError.ReportWithoutCrash(e))
            {
                return VSConstants.E_FAIL;
            }
        }

        private unsafe void SetFileUpdates(
            IDebugUpdateInMemoryPE2 updater,
            List<KeyValuePair<DocumentId, ImmutableArray<LineChange>>> edits)
        {
            int totalEditCount = edits.Sum(e => e.Value.Length);
            if (totalEditCount == 0)
            {
                return;
            }

            var lineUpdates = new LINEUPDATE[totalEditCount];
            fixed (LINEUPDATE* lineUpdatesPtr = lineUpdates)
            {
                int index = 0;
                var fileUpdates = new FILEUPDATE[edits.Count];
                for (int f = 0; f < fileUpdates.Length; f++)
                {
                    var documentId = edits[f].Key;
                    var deltas = edits[f].Value;

                    fileUpdates[f].FileName = _vsProject.GetDocumentOrAdditionalDocument(documentId).FilePath;
                    fileUpdates[f].LineUpdateCount = (uint)deltas.Length;
                    fileUpdates[f].LineUpdates = (IntPtr)(lineUpdatesPtr + index);

                    for (int l = 0; l < deltas.Length; l++)
                    {
                        lineUpdates[index + l].Line = (uint)deltas[l].OldLine;
                        lineUpdates[index + l].UpdatedLine = (uint)deltas[l].NewLine;
                    }

                    index += deltas.Length;
                }

                // The updater makes a copy of all data, we can release the buffer after the call.
                updater.SetFileUpdates(fileUpdates, (uint)fileUpdates.Length);
            }
        }

        private Deltas EmitProjectDelta()
        {
            Debug.Assert(Thread.CurrentThread.GetApartmentState() == ApartmentState.MTA);
            
            var baseline = _committedBaseline;
            if (baseline == null)
            {
                var baselineMetadata = _moduleMetadataProvider.TryGetBaselineMetadata(_mvid);
                if (baselineMetadata != null)
                {
                    baseline = EmitBaseline.CreateInitialBaseline(
                        baselineMetadata,
                        GetBaselineEncDebugInfo,
                        GetBaselineLocalSignature,
                        HasPortableMetadata(_pdbReader.Value));
                }
            }

            if (baseline == null || baseline.OriginalMetadata.IsDisposed)
            {
                var moduleName = PathUtilities.GetFileName(_vsProject.ObjOutputPath);

                // The metadata blob is guaranteed to not be disposed while BuildForEnc is being executed. 
                // If it is disposed it means it had been disposed when entering BuildForEnc.
                log.Write("Module has been unloaded: module '{0}', project '{1}', , MVID: {2}", moduleName, _vsProject.DisplayName, _mvid.ToString());

                var descriptor = new DiagnosticDescriptor(
                    "ENC0001",
                    new LocalizableResourceString(nameof(ServicesVSResources.ModuleHasBeenUnloaded), ServicesVSResources.ResourceManager, typeof(ServicesVSResources)),
                    ServicesVSResources.CantApplyChangesModuleHasBeenUnloaded, 
                    DiagnosticCategory.EditAndContinue, 
                    DiagnosticSeverity.Error, 
                    isEnabledByDefault: true, 
                    customTags: DiagnosticCustomTags.EditAndContinue);

                _diagnosticProvider.ReportDiagnostics(
                    new EncErrorId(_encService.DebuggingSession, EditAndContinueDiagnosticUpdateSource.DebuggerErrorId),
                    _encService.DebuggingSession.InitialSolution,
                    _vsProject.Id,
                    new[] { Diagnostic.Create(descriptor, Location.None, moduleName) });

                return null;
            }

            var emitTask = _encService.EditSession.EmitProjectDeltaAsync(_projectBeingEmitted, baseline, default);
            return emitTask.Result;
        }

        private unsafe bool HasPortableMetadata(ISymUnmanagedReader5 symReader)
            => symReader.GetPortableDebugMetadata(out _, out _) == 0;

        private StandaloneSignatureHandle GetBaselineLocalSignature(MethodDefinitionHandle methodHandle)
        {
            Debug.Assert(Thread.CurrentThread.GetApartmentState() == ApartmentState.MTA);

            var symMethod = (ISymUnmanagedMethod2)_pdbReader.Value.GetMethodByVersion(MetadataTokens.GetToken(methodHandle), methodVersion: 1);

            // Compiler generated methods (e.g. async kick-off methods) might not have debug information.
            return symMethod == null ? default : MetadataTokens.StandaloneSignatureHandle(symMethod.GetLocalSignatureToken());
        }

        /// <summary>
        /// Returns EnC debug information for initial version of the specified method.
        /// </summary>
        /// <exception cref="InvalidDataException">The debug information data is corrupt or can't be retrieved from the debugger.</exception>
        private EditAndContinueMethodDebugInformation GetBaselineEncDebugInfo(MethodDefinitionHandle methodHandle)
        {
            Debug.Assert(Thread.CurrentThread.GetApartmentState() == ApartmentState.MTA);
            return GetEditAndContinueMethodDebugInfo(_pdbReader.Value, methodHandle);
        }

        // Unmarshal the symbol reader (being marshalled cross thread from STA -> MTA).
        private static ISymUnmanagedReader5 UnmarshalSymReader(IntPtr stream)
        {
            Debug.Assert(Thread.CurrentThread.GetApartmentState() == ApartmentState.MTA);
            try
            {
                return (ISymUnmanagedReader5)NativeMethods.GetObjectAndRelease(stream);
            }
            catch (Exception exception) when (FatalError.ReportWithoutCrash(exception))
            {
                throw new InvalidDataException(exception.Message, exception);
            }
        }

        private static EditAndContinueMethodDebugInformation GetEditAndContinueMethodDebugInfo(ISymUnmanagedReader3 symReader, MethodDefinitionHandle methodHandle)
        {
            return TryGetPortableEncDebugInfo(symReader, methodHandle, out var info) ? info : GetNativeEncDebugInfo(symReader, methodHandle);
        }

        private static unsafe bool TryGetPortableEncDebugInfo(ISymUnmanagedReader symReader, MethodDefinitionHandle methodHandle, out EditAndContinueMethodDebugInformation info)
        {
            if (!(symReader is ISymUnmanagedReader5 symReader5))
            {
                info = default;
                return false;
            }

            int hr = symReader5.GetPortableDebugMetadataByVersion(version: 1, metadata: out byte* metadata, size: out int size);
            Marshal.ThrowExceptionForHR(hr);

            if (hr != 0)
            {
                info = default;
                return false;
            }

            var pdbReader = new System.Reflection.Metadata.MetadataReader(metadata, size);

            ImmutableArray<byte> GetCdiBytes(Guid kind) =>
                TryGetCustomDebugInformation(pdbReader, methodHandle, kind, out var cdi) ? pdbReader.GetBlobContent(cdi.Value) : default;

            info = EditAndContinueMethodDebugInformation.Create(
                compressedSlotMap: GetCdiBytes(PortableCustomDebugInfoKinds.EncLocalSlotMap),
                compressedLambdaMap: GetCdiBytes(PortableCustomDebugInfoKinds.EncLambdaAndClosureMap));

            return true;
        }

        /// <exception cref="BadImageFormatException">Invalid data format.</exception>
        private static bool TryGetCustomDebugInformation(System.Reflection.Metadata.MetadataReader reader, EntityHandle handle, Guid kind, out CustomDebugInformation customDebugInfo)
        {
            bool foundAny = false;
            customDebugInfo = default;
            foreach (var infoHandle in reader.GetCustomDebugInformation(handle))
            {
                var info = reader.GetCustomDebugInformation(infoHandle);
                var id = reader.GetGuid(info.Kind);
                if (id == kind)
                {
                    if (foundAny)
                    {
                        throw new BadImageFormatException();
                    }
                    customDebugInfo = info;
                    foundAny = true;
                }
            }
            return foundAny;
        }

        private static EditAndContinueMethodDebugInformation GetNativeEncDebugInfo(ISymUnmanagedReader3 symReader, MethodDefinitionHandle methodHandle)
        {
            int methodToken = MetadataTokens.GetToken(methodHandle);

            byte[] debugInfo;
            try
            {
                debugInfo = symReader.GetCustomDebugInfo(methodToken, methodVersion: 1);
            }
            catch (ArgumentOutOfRangeException)
            {
                // Sometimes the debugger returns the HRESULT for ArgumentOutOfRangeException, rather than E_FAIL,
                // for methods without custom debug info (https://github.com/dotnet/roslyn/issues/4138).
                debugInfo = null;
            }
            catch (Exception e) when (FatalError.ReportWithoutCrash(e)) // likely a bug in the compiler/debugger
            {
                throw new InvalidDataException(e.Message, e);
            }

            try
            {
                ImmutableArray<byte> localSlots, lambdaMap;
                if (debugInfo != null)
                {
                    localSlots = CustomDebugInfoReader.TryGetCustomDebugInfoRecord(debugInfo, CustomDebugInfoKind.EditAndContinueLocalSlotMap);
                    lambdaMap = CustomDebugInfoReader.TryGetCustomDebugInfoRecord(debugInfo, CustomDebugInfoKind.EditAndContinueLambdaMap);
                }
                else
                {
                    localSlots = lambdaMap = default;
                }

                return EditAndContinueMethodDebugInformation.Create(localSlots, lambdaMap);
            }
            catch (InvalidOperationException e) when (FatalError.ReportWithoutCrash(e)) // likely a bug in the compiler/debugger
            {
                // TODO: CustomDebugInfoReader should throw InvalidDataException
                throw new InvalidDataException(e.Message, e);
            }
        }

        public int EncApplySucceeded(int hrApplyResult)
        {
            try
            {
                log.Write("Change applied to {0}", _vsProject.DisplayName);
                Debug.Assert(IsDebuggable);
                Debug.Assert(_encService.EditSession != null);
                Debug.Assert(!_encService.EditSession.StoppedAtException);
                Debug.Assert(_pendingBaseline != null);

                // Since now on until exiting the break state, we consider the changes applied and the project state should be NoChanges.
                _changesApplied = true;

                _committedBaseline = _pendingBaseline;
                _pendingBaseline = null;

                return VSConstants.S_OK;
            }
            catch (Exception e) when (FatalError.ReportWithoutCrash(e))
            {
                return VSConstants.E_FAIL;
            }
        }

        /// <summary>
        /// Called when changes are being applied.
        /// </summary>
        /// <param name="exceptionRegionId">
        /// The value of <see cref="ShellInterop.ENC_EXCEPTION_SPAN.id"/>. 
        /// Set by <see cref="GetExceptionSpans(uint, ShellInterop.ENC_EXCEPTION_SPAN[], ref uint)"/> to the index into <see cref="_exceptionRegions"/>. 
        /// </param>
        /// <param name="ptsNewPosition">Output value holder.</param>
        public int GetCurrentExceptionSpanPosition(uint exceptionRegionId, VsTextSpan[] ptsNewPosition)
        {
            try
            {
                using (NonReentrantContext)
                {
                    Debug.Assert(IsDebuggable);
                    Debug.Assert(_encService.EditSession != null);
                    Debug.Assert(!_encService.EditSession.StoppedAtException);
                    Debug.Assert(ptsNewPosition.Length == 1);

                    var exceptionRegion = _exceptionRegions[(int)exceptionRegionId];

                    var session = _encService.EditSession;
                    var asid = _activeStatementIds[exceptionRegion.ActiveStatementId];

                    var document = _projectBeingEmitted.GetDocument(asid.DocumentId);
                    var analysis = session.GetDocumentAnalysis(document).GetValue(default);
                    var regions = analysis.ExceptionRegions;

                    // the method shouldn't be called in presence of errors:
                    Debug.Assert(!analysis.HasChangesAndErrors);
                    Debug.Assert(!regions.IsDefault);

                    // Absence of rude edits guarantees that the exception regions around AS haven't semantically changed.
                    // Only their spans might have changed.
                    ptsNewPosition[0] = regions[asid.Ordinal][exceptionRegion.Ordinal].ToVsTextSpan();
                }

                return VSConstants.S_OK;
            }
            catch (Exception e) when (FatalError.ReportWithoutCrash(e))
            {
                return VSConstants.E_FAIL;
            }
        }

        private static Lazy<ISymUnmanagedReader5> MarshalPdbReader(IDebugUpdateInMemoryPE2 updater)
        {
            // ISymUnmanagedReader can only be accessed from an MTA thread, however, we need
            // fetch the IUnknown instance (call IENCSymbolReaderProvider.GetSymbolReader) here
            // in the STA.  To further complicate things, we need to return synchronously from
            // this method.  Waiting for the MTA thread to complete so we can return synchronously
            // blocks the STA thread, so we need to make sure the CLR doesn't try to marshal
            // ISymUnmanagedReader calls made in an MTA back to the STA for execution (if this
            // happens we'll be deadlocked).  We'll use CoMarshalInterThreadInterfaceInStream to
            // achieve this.  First, we'll marshal the object in a Stream and pass a Stream pointer
            // over to the MTA.  In the MTA, we'll get the Stream from the pointer and unmarshal
            // the object.  The reader object was originally created on an MTA thread, and the
            // instance we retrieved in the STA was a proxy.  When we unmarshal the Stream in the
            // MTA, it "unwraps" the proxy, allowing us to directly call the implementation.
            // Another way to achieve this would be for the symbol reader to implement IAgileObject,
            // but the symbol reader we use today does not.  If that changes, we should consider
            // removing this marshal/unmarshal code.
            updater.GetENCDebugInfo(out IENCDebugInfo debugInfo);

            var symbolReaderProvider = (IENCSymbolReaderProvider)debugInfo;
            symbolReaderProvider.GetSymbolReader(out object pdbReaderObjSta);
            if (Marshal.IsComObject(pdbReaderObjSta))
            {
                int hr = NativeMethods.GetStreamForObject(pdbReaderObjSta, out IntPtr stream);
                Marshal.ReleaseComObject(pdbReaderObjSta);
                Marshal.ThrowExceptionForHR(hr);

                return new Lazy<ISymUnmanagedReader5>(() => UnmarshalSymReader(stream));
            }
            else
            {
                var managedSymReader = (ISymUnmanagedReader5)pdbReaderObjSta;
                return new Lazy<ISymUnmanagedReader5>(() => managedSymReader);
            }
        }

        #region Testing 

#if DEBUG
        // Fault injection:
        // If set we'll fail to read MVID of specified projects to test error reporting.
        internal static ImmutableArray<string> InjectMvidReadingFailure;

        private void InjectFault_MvidRead()
        {
            if (!InjectMvidReadingFailure.IsDefault && InjectMvidReadingFailure.Contains(_vsProject.DisplayName))
            {
                throw new IOException("Fault injection");
            }
        }
#else
        [Conditional("DEBUG")]
        private void InjectFault_MvidRead()
        {
        }
#endif
        #endregion
    }
}
