﻿// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.DebugEngineHost;
using Microsoft.DebugEngineHost.VSCode;
using Microsoft.VisualStudio.Debugger.Interop;
using Microsoft.VisualStudio.Debugger.Interop.DAP;
using Microsoft.VisualStudio.Shared.VSCodeDebugProtocol;
using Microsoft.VisualStudio.Shared.VSCodeDebugProtocol.Messages;
using Microsoft.VisualStudio.Shared.VSCodeDebugProtocol.Utilities;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using OpenDebug;
using OpenDebug.CustomProtocolObjects;
using OpenDebugAD7.AD7Impl;
using ProtocolMessages = Microsoft.VisualStudio.Shared.VSCodeDebugProtocol.Messages;

namespace OpenDebugAD7
{
    internal class AD7DebugSession : DebugAdapterBase, IDebugPortNotify2, IDebugEventCallback2
    {
        // This is a general purpose lock. Don't hold it across long operations.
        private readonly object m_lock = new object();

        private IDebugProcess2 m_process;
        private string m_processName;
        private IDebugEngineLaunch2 m_engineLaunch;
        private IDebugEngine2 m_engine;
        private EngineConfiguration m_engineConfiguration;
        private AD7Port m_port;

        private readonly DebugEventLogger m_logger;
        private readonly Dictionary<string, Dictionary<int, IDebugPendingBreakpoint2>> m_breakpoints;
        private Dictionary<string, IDebugPendingBreakpoint2> m_functionBreakpoints;
        private readonly Dictionary<int, ThreadFrameEnumInfo> m_threadFrameEnumInfos = new Dictionary<int, ThreadFrameEnumInfo>();
        private readonly HandleCollection<IDebugStackFrame2> m_frameHandles;

        private IDebugProgram2 m_program;
        private readonly Dictionary<int, IDebugThread2> m_threads = new Dictionary<int, IDebugThread2>();

        private ManualResetEvent m_disconnectedOrTerminated;
        private int m_firstStoppingEvent;
        private uint m_breakCounter = 0;
        private bool m_isAttach;
        private bool m_isCoreDump;
        private bool m_isStopped = false;
        private bool m_isStepping = false;

        private readonly TaskCompletionSource<object> m_configurationDoneTCS = new TaskCompletionSource<object>();

        private readonly SessionConfiguration m_sessionConfig = new SessionConfiguration();

        private PathConverter m_pathConverter = new PathConverter();

        private VariableManager m_variableManager;

        private static Guid s_guidFilterAllLocalsPlusArgs = new Guid("939729a8-4cb0-4647-9831-7ff465240d5f");

        #region Constructor

        public AD7DebugSession(Stream debugAdapterStdIn, Stream debugAdapterStdOut, List<LoggingCategory> loggingCategories)
        {
            // This initializes this.Protocol with the streams
            base.InitializeProtocolClient(debugAdapterStdIn, debugAdapterStdOut);
            Debug.Assert(Protocol != null, "InitializeProtocolClient should have initialized this.Protocol");

            RegisterAD7EventCallbacks();
            m_logger = new DebugEventLogger(Protocol.SendEvent, loggingCategories);

            // Register message logger
            Protocol.LogMessage += m_logger.TraceLogger_EventHandler;

            m_frameHandles = new HandleCollection<IDebugStackFrame2>();
            m_breakpoints = new Dictionary<string, Dictionary<int, IDebugPendingBreakpoint2>>();
            m_functionBreakpoints = new Dictionary<string, IDebugPendingBreakpoint2>();
            m_variableManager = new VariableManager();
        }

        #endregion

        #region Utility

        private void SendTelemetryEvent(string eventName, KeyValuePair<string, object>[] eventProperties)
        {
            Dictionary<string, object> propertiesDictionary = null;
            if (eventProperties != null)
            {
                propertiesDictionary = new Dictionary<string, object>();
                foreach (var pair in eventProperties)
                {
                    propertiesDictionary[pair.Key] = pair.Value;
                }
            }

            m_logger.Write(LoggingCategory.Telemetry, eventName, propertiesDictionary);
        }

        private ProtocolException CreateProtocolExceptionAndLogTelemetry(string telemetryEventName, int error, string message)
        {
            DebuggerTelemetry.ReportError(telemetryEventName, error);
            return new ProtocolException(message, new Message(error, message));
        }

        private bool ValidateProgramPath(ref string program)
        {
            // Make sure the slashes go in the correct direction
            char directorySeparatorChar = Path.DirectorySeparatorChar;
            char wrongSlashChar = directorySeparatorChar == '\\' ? '/' : '\\';

            if (program.Contains(wrongSlashChar))
            {
                program = program.Replace(wrongSlashChar, directorySeparatorChar);
            }

            program = m_pathConverter.ConvertLaunchPathForVsCode(program);
            if (!File.Exists(program))
            {
                // On Windows, check if we are just missing a '.exe' from the file name. This way we can use the same
                // launch.json on all platforms.
                if (Utilities.IsWindows())
                {
                    if (!program.EndsWith(".", StringComparison.OrdinalIgnoreCase) && !program.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                    {
                        string programWithExe = program + ".exe";
                        if (File.Exists(programWithExe))
                        {
                            program = programWithExe;
                            return true;
                        }
                    }
                }
                return false;
            }
            return true;
        }

        private void SetCommonDebugSettings(Dictionary<string, JToken> args, out int sourceFileMappings)
        {
            // Save the Just My Code setting. We will set it once the engine is created.
            m_sessionConfig.JustMyCode = args.GetValueAsBool("justMyCode").GetValueOrDefault(m_sessionConfig.JustMyCode);
            m_sessionConfig.RequireExactSource = args.GetValueAsBool("requireExactSource").GetValueOrDefault(m_sessionConfig.RequireExactSource);
            m_sessionConfig.EnableStepFiltering = args.GetValueAsBool("enableStepFiltering").GetValueOrDefault(m_sessionConfig.EnableStepFiltering);

            JObject logging = args.GetValueAsObject("logging");

            if (logging != null)
            {
                m_logger.SetLoggingConfiguration(LoggingCategory.Exception, logging.GetValueAsBool("exceptions").GetValueOrDefault(true));
                m_logger.SetLoggingConfiguration(LoggingCategory.Module, logging.GetValueAsBool("moduleLoad").GetValueOrDefault(true));
                m_logger.SetLoggingConfiguration(LoggingCategory.StdOut, logging.GetValueAsBool("programOutput").GetValueOrDefault(true));
                m_logger.SetLoggingConfiguration(LoggingCategory.StdErr, logging.GetValueAsBool("programOutput").GetValueOrDefault(true));

                bool? engineLogging = logging.GetValueAsBool("engineLogging");
                if (engineLogging.HasValue)
                {
                    m_logger.SetLoggingConfiguration(LoggingCategory.EngineLogging, engineLogging.Value);
                    HostLogger.EnableHostLogging();
                    HostLogger.Instance.LogCallback = s => m_logger.WriteLine(LoggingCategory.EngineLogging, s);
                }

                bool? trace = logging.GetValueAsBool("trace");
                bool? traceResponse = logging.GetValueAsBool("traceResponse");
                if (trace.HasValue || traceResponse.HasValue)
                {
                    m_logger.SetLoggingConfiguration(LoggingCategory.AdapterTrace, (trace.GetValueOrDefault(false)) || (traceResponse.GetValueOrDefault(false)));
                }

                if (traceResponse.HasValue)
                {
                    m_logger.SetLoggingConfiguration(LoggingCategory.AdapterResponse, traceResponse.Value);
                }
            }

            sourceFileMappings = 0;
            Dictionary<string, string> sourceFileMap = null;
            {
                dynamic sourceFileMapProperty = args.GetValueAsObject("sourceFileMap");
                if (sourceFileMapProperty != null)
                {
                    try
                    {
                        sourceFileMap = sourceFileMapProperty.ToObject<Dictionary<string, string>>();
                        sourceFileMappings = sourceFileMap.Count();
                    }
                    catch (Exception e)
                    {
                        SendMessageEvent(MessagePrefix.Error, "Configuration for 'sourceFileMap' has a format error and will be ignored.\nException: " + e.Message);
                        sourceFileMap = null;
                    }
                }
            }
            m_pathConverter.m_pathMapper = new PathMapper(sourceFileMap);
        }

        private ProtocolException VerifyLocalProcessId(string processId, string telemetryEventName, out int pid)
        {
            ProtocolException protocolException = VerifyProcessId(processId, telemetryEventName, out pid);

            if (protocolException != null)
            {
                return protocolException;
            }

            try
            {
                Process.GetProcessById(pid);
            }
            catch (ArgumentException)
            {
                return CreateProtocolExceptionAndLogTelemetry(telemetryEventName, 1006, string.Format(CultureInfo.CurrentCulture, "attach: no process with the given id:{0} found", pid));
            }

            return null;
        }

        private ProtocolException VerifyProcessId(string processId, string telemetryEventName, out int pid)
        {
            if (!int.TryParse(processId, out pid))
            {
                return CreateProtocolExceptionAndLogTelemetry(telemetryEventName, 1005, "attach: unable to parse the process id");
            }

            if (pid == 0)
            {
                return CreateProtocolExceptionAndLogTelemetry(telemetryEventName, 1008, "attach: launch.json must be configured. Change 'processId' to the process you want to debug.");
            }

            return null;
        }

        private IList<Tracepoint> GetTracepoints(IDebugBreakpointEvent2 debugEvent)
        {
            IList<Tracepoint> tracepoints = new List<Tracepoint>();

            if (debugEvent != null)
            {
                debugEvent.EnumBreakpoints(out IEnumDebugBoundBreakpoints2 pBoundBreakpoints);
                IDebugBoundBreakpoint2[] boundBp = new IDebugBoundBreakpoint2[1];

                uint numReturned = 0;
                while (pBoundBreakpoints.Next(1, boundBp, ref numReturned) == HRConstants.S_OK && numReturned == 1)
                {
                    if (boundBp[0].GetPendingBreakpoint(out IDebugPendingBreakpoint2 ppPendingBreakpoint) == HRConstants.S_OK &&
                        ppPendingBreakpoint.GetBreakpointRequest(out IDebugBreakpointRequest2 ppBPRequest) == HRConstants.S_OK &&
                        ppBPRequest is AD7BreakPointRequest ad7BreakpointRequest &&
                        ad7BreakpointRequest.HasTracepoint)
                    {
                        tracepoints.Add(ad7BreakpointRequest.Tracepoint);
                    }
                }
            }

            return tracepoints;
        }

        /// <summary>
        /// Converts a string expression into a IDebugProperty2 with given frame and evaluation flags.
        /// </summary>
        /// <param name="expression">Expression to evaluate</param>
        /// <param name="frame">Frame to execute the expression on</param>
        /// <param name="additionalEvalFlags">Additional evalFlags besides the flags normally used for vs watch window.</param>
        /// <param name="additionalDapEvalFlags">Additional flags besides NONE.</param>
        /// <param name="property">Property that is evaluated from 'expression'</param>
        /// <param name="error">Error when evaluating 'expression'</param>
        /// <returns>An HRESULT signifiyng if it sucessfuly obtainied a property or not from the expression.</returns>
        private int GetIDebugPropertyFromExpression(string expression, IDebugStackFrame2 frame, enum_EVALFLAGS additionalEvalFlags, DAPEvalFlags additionalDapEvalFlags, out IDebugProperty2 property, out string error)
        {
            property = null;
            error = string.Empty;

            int hr;
            IDebugExpressionContext2 expressionContext;
            hr = frame.GetExpressionContext(out expressionContext);
            if (hr >= 0)
            {
                IDebugExpression2 expressionObject;
                uint errorIndex;
                hr = expressionContext.ParseText(expression, enum_PARSEFLAGS.PARSE_EXPRESSION, Constants.ParseRadix, out expressionObject, out error, out errorIndex);
                if (!string.IsNullOrEmpty(error))
                {
                    // TODO: Is this how errors should be returned?
                    DebuggerTelemetry.ReportError(DebuggerTelemetry.TelemetryEvaluateEventName, 4001, "Error parsing expression");
                    return HRConstants.E_FAIL;
                }

                // NOTE: This is the same as what vssdebug normally passes for the watch window
                enum_EVALFLAGS flags = enum_EVALFLAGS.EVAL_RETURNVALUE |
                    enum_EVALFLAGS.EVAL_NOEVENTS |
                    (enum_EVALFLAGS)enum_EVALFLAGS110.EVAL110_FORCE_REAL_FUNCEVAL |
                    additionalEvalFlags;

                DAPEvalFlags dapEvalFlags = DAPEvalFlags.NONE | 
                    additionalDapEvalFlags;

                if (expressionObject is IDebugExpressionDAP expressionDapObject)
                {
                    hr = expressionDapObject.EvaluateSync(flags, dapEvalFlags, Constants.EvaluationTimeout, null, out property);
                }
                else
                {
                    hr = expressionObject.EvaluateSync(flags, Constants.EvaluationTimeout, null, out property);
                }
            }

            return hr;
        }

        #endregion

        #region AD7EventHandlers helper methods

        public void BeforeContinue()
        {
            if (!m_isCoreDump)
            {
                m_isStepping = false;
                m_isStopped = false;
                m_variableManager.Reset();
                m_frameHandles.Reset();
                m_threadFrameEnumInfos.Clear();
            }
        }

        public void Stopped(IDebugThread2 thread)
        {
            Debug.Assert(m_variableManager.IsEmpty(), "Why do we have variable handles?");
            Debug.Assert(m_frameHandles.IsEmpty, "Why do we have frame handles?");
            Debug.Assert(m_threadFrameEnumInfos.Count == 0, "Why do we have thread frame enums?");
            m_isStopped = true;
        }

        internal void FireStoppedEvent(IDebugThread2 thread, StoppedEvent.ReasonValue reason, string text = null)
        {
            Stopped(thread);

            // Switch to another thread as engines may not expect to be called back on their event thread
            ThreadPool.QueueUserWorkItem((o) =>
            {
                IEnumDebugFrameInfo2 frameInfoEnum;
                thread.EnumFrameInfo(enum_FRAMEINFO_FLAGS.FIF_FRAME | enum_FRAMEINFO_FLAGS.FIF_FLAGS, Constants.EvaluationRadix, out frameInfoEnum);

                TextPositionTuple textPosition = TextPositionTuple.Nil;
                if (frameInfoEnum != null)
                {
                    while (true)
                    {
                        FRAMEINFO[] frameInfoArray = new FRAMEINFO[1];
                        uint cFetched = 0;
                        frameInfoEnum.Next(1, frameInfoArray, ref cFetched);
                        if (cFetched != 1)
                        {
                            break;
                        }

                        if (AD7Utils.IsAnnotatedFrame(ref frameInfoArray[0]))
                        {
                            continue;
                        }

                        textPosition = TextPositionTuple.GetTextPositionOfFrame(m_pathConverter, frameInfoArray[0].m_pFrame) ?? TextPositionTuple.Nil;
                        break;
                    }
                }

                lock (m_lock)
                {
                    m_breakCounter++;
                }
                Protocol.SendEvent(new OpenDebugStoppedEvent()
                {
                    Reason = reason,
                    Source = textPosition.Source,
                    Line = textPosition.Line,
                    Column = textPosition.Column,
                    Text = text,
                    ThreadId = thread.Id()
                });
            });

            if (Interlocked.Exchange(ref m_firstStoppingEvent, 1) == 0)
            {
                m_logger.WriteLine(LoggingCategory.DebuggerStatus, AD7Resources.DebugConsoleStartMessage);
            }
        }

        private void SendDebugCompletedTelemetry()
        {
            Dictionary<string, object> properties = new Dictionary<string, object>();
            lock (m_lock)
            {
                properties.Add(DebuggerTelemetry.TelemetryBreakCounter, m_breakCounter);
            }
            DebuggerTelemetry.ReportEvent(DebuggerTelemetry.TelemetryDebugCompletedEventName, properties);
        }

        private static IEnumerable<IDebugBoundBreakpoint2> GetBoundBreakpoints(IDebugBreakpointBoundEvent2 breakpointBoundEvent)
        {
            int hr;
            IEnumDebugBoundBreakpoints2 boundBreakpointsEnum;
            hr = breakpointBoundEvent.EnumBoundBreakpoints(out boundBreakpointsEnum);
            if (hr != HRConstants.S_OK)
            {
                return Enumerable.Empty<IDebugBoundBreakpoint2>();
            }

            uint bufferSize;
            hr = boundBreakpointsEnum.GetCount(out bufferSize);
            if (hr != HRConstants.S_OK)
            {
                return Enumerable.Empty<IDebugBoundBreakpoint2>();
            }

            IDebugBoundBreakpoint2[] boundBreakpoints = new IDebugBoundBreakpoint2[bufferSize];
            uint fetched = 0;
            hr = boundBreakpointsEnum.Next(bufferSize, boundBreakpoints, ref fetched);
            if (hr != HRConstants.S_OK || fetched != bufferSize)
            {
                return Enumerable.Empty<IDebugBoundBreakpoint2>();
            }

            return boundBreakpoints;
        }

        private int? GetBoundBreakpointLineNumber(IDebugBoundBreakpoint2 boundBreakpoint)
        {
            int hr;
            IDebugBreakpointResolution2 breakpointResolution;
            hr = boundBreakpoint.GetBreakpointResolution(out breakpointResolution);
            if (hr != HRConstants.S_OK)
            {
                return null;
            }

            BP_RESOLUTION_INFO[] resolutionInfo = new BP_RESOLUTION_INFO[1];
            hr = breakpointResolution.GetResolutionInfo(enum_BPRESI_FIELDS.BPRESI_BPRESLOCATION, resolutionInfo);
            if (hr != HRConstants.S_OK)
            {
                return null;
            }

            BP_RESOLUTION_LOCATION location = resolutionInfo[0].bpResLocation;
            enum_BP_TYPE bpType = (enum_BP_TYPE)location.bpType;
            if (bpType != enum_BP_TYPE.BPT_CODE || location.unionmember1 == IntPtr.Zero)
            {
                return null;
            }

            IDebugCodeContext2 codeContext;
            try
            {
                codeContext = HostMarshal.GetCodeContextForIntPtr(location.unionmember1);
                HostMarshal.ReleaseCodeContextId(location.unionmember1);
                location.unionmember1 = IntPtr.Zero;
            }
            catch (ArgumentException)
            {
                return null;
            }
            IDebugDocumentContext2 docContext;
            hr = codeContext.GetDocumentContext(out docContext);
            if (hr != HRConstants.S_OK)
            {
                return null;
            }

            // VSTS 237376: Shared library compiled without symbols will still bind a bp, but not have a docContext
            if (null == docContext)
            {
                return null;
            }

            TEXT_POSITION[] begin = new TEXT_POSITION[1];
            TEXT_POSITION[] end = new TEXT_POSITION[1];
            hr = docContext.GetStatementRange(begin, end);
            if (hr != HRConstants.S_OK)
            {
                return null;
            }

            return m_pathConverter.ConvertDebuggerLineToClient((int)begin[0].dwLine);
        }

        private enum MessagePrefix
        {
            None,
            Warning,
            Error
        };

        private class CurrentLaunchState
        {
            public Tuple<MessagePrefix, string> CurrentError { get; set; }
        }

        private CurrentLaunchState m_currentLaunchState;

        private void SendMessageEvent(MessagePrefix prefix, string text)
        {
            string prefixString = string.Empty;
            LoggingCategory category = LoggingCategory.DebuggerStatus;
            switch (prefix)
            {
                case MessagePrefix.Warning:
                    prefixString = AD7Resources.Prefix_Warning;
                    category = LoggingCategory.DebuggerError;
                    break;
                case MessagePrefix.Error:
                    prefixString = AD7Resources.Prefix_Error;
                    category = LoggingCategory.DebuggerError;
                    break;
            }

            m_logger.WriteLine(category, prefixString + text);
        }

        private VariablesResponse VariablesFromFrame(IDebugStackFrame2 frame)
        {
            VariablesResponse response = new VariablesResponse();

            uint n;
            IEnumDebugPropertyInfo2 varEnum;
            if (frame.EnumProperties(enum_DEBUGPROP_INFO_FLAGS.DEBUGPROP_INFO_PROP, 10, ref s_guidFilterAllLocalsPlusArgs, 0, out n, out varEnum) == HRConstants.S_OK)
            {
                DEBUG_PROPERTY_INFO[] props = new DEBUG_PROPERTY_INFO[1];
                uint nProps;
                while (varEnum.Next(1, props, out nProps) == HRConstants.S_OK)
                {
                    response.Variables.Add(m_variableManager.CreateVariable(props[0].pProperty, GetDefaultPropertyInfoFlags()));
                }
            }

            return response;
        }

        public enum_DEBUGPROP_INFO_FLAGS GetDefaultPropertyInfoFlags()
        {
            enum_DEBUGPROP_INFO_FLAGS flags =
                enum_DEBUGPROP_INFO_FLAGS.DEBUGPROP_INFO_NAME |
                enum_DEBUGPROP_INFO_FLAGS.DEBUGPROP_INFO_VALUE |
                enum_DEBUGPROP_INFO_FLAGS.DEBUGPROP_INFO_TYPE |
                enum_DEBUGPROP_INFO_FLAGS.DEBUGPROP_INFO_ATTRIB |
                enum_DEBUGPROP_INFO_FLAGS.DEBUGPROP_INFO_PROP |
                enum_DEBUGPROP_INFO_FLAGS.DEBUGPROP_INFO_FULLNAME |
                (enum_DEBUGPROP_INFO_FLAGS)enum_DEBUGPROP_INFO_FLAGS110.DEBUGPROP110_INFO_FORCE_REAL_FUNCEVAL;

            if (m_sessionConfig.JustMyCode)
            {
                flags |= (enum_DEBUGPROP_INFO_FLAGS)enum_DEBUGPROP_INFO_FLAGS110.DEBUGPROP110_INFO_NO_NONPUBLIC_MEMBERS;
            }

            return flags;
        }

        private void SetAllExceptions(enum_EXCEPTION_STATE state)
        {
            foreach (ExceptionSettings.CategoryConfiguration category in m_engineConfiguration.ExceptionSettings.Categories)
            {
                var exceptionInfo = new EXCEPTION_INFO[1];
                exceptionInfo[0].dwState = state;
                exceptionInfo[0].guidType = category.Id;
                exceptionInfo[0].bstrExceptionName = category.Name;
                m_engine.SetException(exceptionInfo);
            }
        }

        private void StepInternal(int threadId, enum_STEPKIND stepKind, enum_STEPUNIT stepUnit, string errorMessage)
        {
            // If we are already running ignore additional step requests
            if (m_isStopped)
            {
                IDebugThread2 thread = null;
                lock (m_threads)
                {
                    if (!m_threads.TryGetValue(threadId, out thread))
                    {
                        throw new AD7Exception(errorMessage);
                    }
                }

                BeforeContinue();
                ErrorBuilder builder = new ErrorBuilder(() => errorMessage);
                m_isStepping = true;
                try
                {
                    builder.CheckHR(m_program.Step(thread, stepKind, stepUnit));
                }
                catch (AD7Exception)
                {
                    m_isStopped = true;
                    throw;
                }
            }
        }

        #endregion

        #region DebugAdapterBase

        protected override void HandleInitializeRequestAsync(IRequestResponder<InitializeArguments, InitializeResponse> responder)
        {
            InitializeArguments arguments = responder.Arguments;

            m_engineConfiguration = EngineConfiguration.TryGet(arguments.AdapterID);

            m_engine = (IDebugEngine2)m_engineConfiguration.LoadEngine();

            TypeInfo engineType = m_engine.GetType().GetTypeInfo();
            HostTelemetry.InitializeTelemetry(SendTelemetryEvent, engineType, m_engineConfiguration.AdapterId);
            DebuggerTelemetry.InitializeTelemetry(Protocol.SendEvent, engineType, typeof(Host).GetTypeInfo(), m_engineConfiguration.AdapterId);

            HostOutputWindow.InitializeLaunchErrorCallback((error) => m_logger.WriteLine(LoggingCategory.DebuggerError, error));

            m_engineLaunch = (IDebugEngineLaunch2)m_engine;
            m_engine.SetRegistryRoot(m_engineConfiguration.AdapterId);
            m_port = new AD7Port(this);
            m_disconnectedOrTerminated = new ManualResetEvent(false);
            m_firstStoppingEvent = 0;

            m_pathConverter.ClientLinesStartAt1 = arguments.LinesStartAt1.GetValueOrDefault(true);

            // Default is that they are URIs
            m_pathConverter.ClientPathsAreURI = !(arguments.PathFormat.GetValueOrDefault(InitializeArguments.PathFormatValue.Unknown) == InitializeArguments.PathFormatValue.Path);

            // If the UI supports RunInTerminal, then register the callback.
            if (arguments.SupportsRunInTerminalRequest.GetValueOrDefault(false))
            {
                HostRunInTerminal.RegisterRunInTerminalCallback((title, cwd, useExternalConsole, commandArgs, env, success, error) =>
                {
                    RunInTerminalRequest request = new RunInTerminalRequest()
                    {
                        Arguments = commandArgs.ToList<string>(),
                        Kind = useExternalConsole ? RunInTerminalArguments.KindValue.External : RunInTerminalArguments.KindValue.Integrated,
                        Title = title,
                        Cwd = cwd,
                        Env = env
                    };

                    Protocol.SendClientRequest(
                        request,
                        (args, responseBody) =>
                        {
                            // responseBody can be null
                            success(responseBody?.ProcessId);
                        },
                        (args, exception) =>
                        {
                            new OutputEvent() { Category = OutputEvent.CategoryValue.Stderr, Output = exception.ToString() };
                            Protocol.SendEvent(new TerminatedEvent());
                            error(exception.ToString());
                        });
                });
            }

            InitializeResponse initializeResponse = new InitializeResponse()
            {
                SupportsConfigurationDoneRequest = true,
                SupportsEvaluateForHovers = true,
                SupportsSetVariable = true,
                SupportsFunctionBreakpoints = m_engineConfiguration.FunctionBP,
                SupportsConditionalBreakpoints = m_engineConfiguration.ConditionalBP,
                ExceptionBreakpointFilters = m_engineConfiguration.ExceptionSettings.ExceptionBreakpointFilters.Select(item => new ExceptionBreakpointsFilter() { Default = item.@default, Filter = item.filter, Label = item.label }).ToList(),
                SupportsClipboardContext = m_engineConfiguration.ClipboardContext,
                SupportsLogPoints = true,
                SupportsReadMemoryRequest = true
            };

            responder.SetResponse(initializeResponse);
        }

        protected override void HandleLaunchRequestAsync(IRequestResponder<LaunchArguments> responder)
        {
            const string telemetryEventName = DebuggerTelemetry.TelemetryLaunchEventName;

            int hr;
            DateTime launchStartTime = DateTime.Now;

            string mimode = responder.Arguments.ConfigurationProperties.GetValueAsString("MIMode");
            string program = responder.Arguments.ConfigurationProperties.GetValueAsString("program")?.Trim();
            if (string.IsNullOrEmpty(program))
            {
                responder.SetError(CreateProtocolExceptionAndLogTelemetry(telemetryEventName, 1001, "launch: property 'program' is missing or empty"));
                return;
            }

            // If program is still in the default state, raise error
            if (program.EndsWith(">", StringComparison.Ordinal) && program.Contains('<'))
            {
                responder.SetError(CreateProtocolExceptionAndLogTelemetry(telemetryEventName, 1001, "launch: launch.json must be configured. Change 'program' to the path to the executable file that you would like to debug."));
                return;
            }

            // Should not have a pid in launch
            if (responder.Arguments.ConfigurationProperties.ContainsKey("processId"))
            {
                responder.SetError(CreateProtocolExceptionAndLogTelemetry(telemetryEventName, 1001, "launch: The parameter: 'processId' should not be specified on Launch. Please use request type: 'attach'"));
                return;
            }

            JToken pipeTransport = responder.Arguments.ConfigurationProperties.GetValueAsObject("pipeTransport");
            string miDebuggerServerAddress = responder.Arguments.ConfigurationProperties.GetValueAsString("miDebuggerServerAddress");

            // Pipe trasport can talk to remote machines so paths and files should not be checked in this case.
            bool skipFilesystemChecks = (pipeTransport != null || miDebuggerServerAddress != null);

            // For a remote scenario, we assume whatever input user has provided is correct.
            // The target remote could be any OS, so we don't try to change anything.
            if (!skipFilesystemChecks)
            {
                if (!ValidateProgramPath(ref program))
                {
                    responder.SetError(CreateProtocolExceptionAndLogTelemetry(telemetryEventName, 1002, String.Format(CultureInfo.CurrentCulture, "launch: program '{0}' does not exist", program)));
                    return;
                }
            }

            string workingDirectory = responder.Arguments.ConfigurationProperties.GetValueAsString("cwd");
            if (string.IsNullOrEmpty(workingDirectory))
            {
                responder.SetError(CreateProtocolExceptionAndLogTelemetry(telemetryEventName, 1003, "launch: property 'cwd' is missing or empty"));
                return;
            }

            if (!skipFilesystemChecks)
            {
                workingDirectory = m_pathConverter.ConvertLaunchPathForVsCode(workingDirectory);
                if (!Directory.Exists(workingDirectory))
                {
                    responder.SetError(CreateProtocolExceptionAndLogTelemetry(telemetryEventName, 1004, String.Format(CultureInfo.CurrentCulture, "launch: workingDirectory '{0}' does not exist", workingDirectory)));
                    return;
                }
            }

            int sourceFileMappings = 0;
            SetCommonDebugSettings(responder.Arguments.ConfigurationProperties, sourceFileMappings: out sourceFileMappings);

            bool success = false;
            try
            {
                lock (m_lock)
                {
                    Debug.Assert(m_currentLaunchState == null, "Concurrent launches??");
                    m_currentLaunchState = new CurrentLaunchState();
                }
                var eb = new ErrorBuilder(() => AD7Resources.Error_Scenario_Launch);

                // Don't convert the workingDirectory string if we are a pipeTransport connection. We are assuming that the user has the correct directory separaters for their target OS
                string workingDirectoryString = pipeTransport != null ? workingDirectory : m_pathConverter.ConvertClientPathToDebugger(workingDirectory);

                bool debugServerUsed = false;
                bool isOpenOCD = false;
                bool stopAtEntrypoint;
                bool visualizerFileUsed;
                string launchOptions = MILaunchOptions.CreateLaunchOptions(
                    program: program,
                    workingDirectory: workingDirectoryString,
                    args: JsonConvert.SerializeObject(responder.Arguments.ConfigurationProperties),
                    isPipeLaunch: responder.Arguments.ConfigurationProperties.ContainsKey("pipeTransport"),
                    stopAtEntry: out stopAtEntrypoint,
                    isCoreDump: out m_isCoreDump,
                    debugServerUsed: out debugServerUsed,
                    isOpenOCD: out isOpenOCD,
                    visualizerFileUsed: out visualizerFileUsed);

                m_sessionConfig.StopAtEntrypoint = stopAtEntrypoint;

                m_processName = program;

                enum_LAUNCH_FLAGS flags = enum_LAUNCH_FLAGS.LAUNCH_DEBUG;
                if (responder.Arguments.NoDebug.GetValueOrDefault(false))
                {
                    flags = enum_LAUNCH_FLAGS.LAUNCH_NODEBUG;
                }

                // Then attach
                hr = m_engineLaunch.LaunchSuspended(null,
                    m_port,
                    program,
                    null,
                    null,
                    null,
                    launchOptions,
                    flags,
                    0,
                    0,
                    0,
                    this,
                    out m_process);
                if (hr != HRConstants.S_OK)
                {
                    // If the engine raised a message via an error event, fire that instead
                    if (hr == HRConstants.E_ABORT)
                    {
                        string message;
                        lock (m_lock)
                        {
                            message = m_currentLaunchState?.CurrentError?.Item2;
                            m_currentLaunchState = null;
                        }
                        if (message != null)
                        {
                            responder.SetError(CreateProtocolExceptionAndLogTelemetry(telemetryEventName, 1005, message));
                            return;
                        }
                    }

                    eb.ThrowHR(hr);
                }

                hr = m_engineLaunch.ResumeProcess(m_process);
                if (hr < 0)
                {
                    // try to terminate the process if we can
                    try
                    {
                        m_engineLaunch.TerminateProcess(m_process);
                    }
                    catch
                    {
                        // Ignore failures since we are already dealing with an error
                    }

                    eb.ThrowHR(hr);
                }

                var properties = new Dictionary<string, object>(StringComparer.Ordinal);

                properties.Add(DebuggerTelemetry.TelemetryIsCoreDump, m_isCoreDump);
                if (debugServerUsed)
                {
                    properties.Add(DebuggerTelemetry.TelemetryUsesDebugServer, isOpenOCD ? "openocd" : "other");
                }
                if (flags.HasFlag(enum_LAUNCH_FLAGS.LAUNCH_NODEBUG))
                {
                    properties.Add(DebuggerTelemetry.TelemetryIsNoDebug, true);
                }

                properties.Add(DebuggerTelemetry.TelemetryVisualizerFileUsed, visualizerFileUsed);
                properties.Add(DebuggerTelemetry.TelemetrySourceFileMappings, sourceFileMappings);
                properties.Add(DebuggerTelemetry.TelemetryMIMode, mimode);

                DebuggerTelemetry.ReportTimedEvent(telemetryEventName, DateTime.Now - launchStartTime, properties);

                success = true;
            }
            catch (Exception e)
            {
                // Instead of failing to launch with the exception, try and wrap it better so that the information is useful for the user.
                responder.SetError(CreateProtocolExceptionAndLogTelemetry(telemetryEventName, 1007, string.Format(CultureInfo.CurrentCulture, AD7Resources.Error_ExceptionOccured, e.InnerException?.ToString() ?? e.ToString())));
                return;
            }
            finally
            {
                // Clear _currentLaunchState
                CurrentLaunchState currentLaunchState;
                lock (m_lock)
                {
                    currentLaunchState = m_currentLaunchState;
                    m_currentLaunchState = null;
                }

                if (!success)
                {
                    m_process = null;
                }

                // If we had an error event that we didn't wind up returning as an exception, raise it as an event
                Tuple<MessagePrefix, string> currentError = currentLaunchState?.CurrentError;
                if (currentError != null)
                {
                    SendMessageEvent(currentError.Item1, currentError.Item2);
                }
            }

            responder.SetResponse(new LaunchResponse());
        }

        protected override void HandleAttachRequestAsync(IRequestResponder<AttachArguments> responder)
        {
            const string telemetryEventName = DebuggerTelemetry.TelemetryAttachEventName;

            // ProcessId can be either a string or an int. We attempt to parse as int, if that does not exist we attempt to parse as a string.
            string processId = responder.Arguments.ConfigurationProperties.GetValueAsInt("processId")?.ToString(CultureInfo.InvariantCulture) ?? responder.Arguments.ConfigurationProperties.GetValueAsString("processId");
            string miDebuggerServerAddress = responder.Arguments.ConfigurationProperties.GetValueAsString("miDebuggerServerAddress");
            DateTime attachStartTime = DateTime.Now;
            JObject pipeTransport = responder.Arguments.ConfigurationProperties.GetValueAsObject("pipeTransport");
            bool isPipeTransport = (pipeTransport != null);
            bool isLocal = string.IsNullOrEmpty(miDebuggerServerAddress) && !isPipeTransport;
            bool visualizerFileUsed = false;
            int sourceFileMappings = 0;
            string mimode = responder.Arguments.ConfigurationProperties.GetValueAsString("MIMode");

            if (isLocal)
            {
                if (string.IsNullOrEmpty(processId))
                {
                    responder.SetError(CreateProtocolExceptionAndLogTelemetry(telemetryEventName, 1001, "attach: property 'processId' needs to be specified"));
                    return;
                }
            }
            else
            {
                string propertyCausingRemote = !string.IsNullOrEmpty(miDebuggerServerAddress) ? "miDebuggerServerAddress" : "pipeTransport";

                if (!string.IsNullOrEmpty(miDebuggerServerAddress) && !string.IsNullOrEmpty(processId))
                {
                    responder.SetError(CreateProtocolExceptionAndLogTelemetry(telemetryEventName, 1002, "attach: 'processId' cannot be used with " + propertyCausingRemote));
                    return;
                }
                else if (isPipeTransport && (string.IsNullOrEmpty(processId) || string.IsNullOrEmpty(pipeTransport.GetValueAsString("debuggerPath"))))
                {
                    responder.SetError(CreateProtocolExceptionAndLogTelemetry(telemetryEventName, 1001, "attach: properties 'processId' and 'debuggerPath' needs to be specified with " + propertyCausingRemote));
                    return;
                }
            }

            int pid = 0;

            ProtocolException protocolException = isLocal ? VerifyLocalProcessId(processId, telemetryEventName, out pid) : VerifyProcessId(processId, telemetryEventName, out pid);

            if (protocolException != null)
            {
                responder.SetError(protocolException);
                return;
            }

            SetCommonDebugSettings(responder.Arguments.ConfigurationProperties, sourceFileMappings: out sourceFileMappings);

            string program = responder.Arguments.ConfigurationProperties.GetValueAsString("program");
            string executable = null;
            string launchOptions = null;
            bool success = false;
            try
            {
                lock (m_lock)
                {
                    Debug.Assert(m_currentLaunchState == null, "Concurrent launches??");
                    m_currentLaunchState = new CurrentLaunchState();
                }
                var eb = new ErrorBuilder(() => AD7Resources.Error_Scenario_Attach);

                if (isPipeTransport)
                {
                    if (string.IsNullOrEmpty(pipeTransport.GetValueAsString("debuggerPath")))
                    {
                        responder.SetError(CreateProtocolExceptionAndLogTelemetry(telemetryEventName, 1011, "debuggerPath is required for attachTransport."));
                        return;
                    }
                    bool debugServerUsed = false;
                    bool isOpenOCD = false;
                    bool stopAtEntrypoint = false;

                    launchOptions = MILaunchOptions.CreateLaunchOptions(
                        program: program,
                        workingDirectory: String.Empty, // No cwd for attach
                        args: JsonConvert.SerializeObject(responder.Arguments.ConfigurationProperties),
                        isPipeLaunch: responder.Arguments.ConfigurationProperties.ContainsKey("pipeTransport"),
                        stopAtEntry: out stopAtEntrypoint,
                        isCoreDump: out m_isCoreDump,
                        debugServerUsed: out debugServerUsed,
                        isOpenOCD: out isOpenOCD,
                        visualizerFileUsed: out visualizerFileUsed);


                    if (string.IsNullOrEmpty(program))
                    {
                        responder.SetError(CreateProtocolExceptionAndLogTelemetry(telemetryEventName, 1009, "attach: property 'program' is missing or empty"));
                        return;
                    }
                    else
                    {
                        executable = program;
                    }
                    m_isAttach = true;
                }
                else
                {
                    if (string.IsNullOrEmpty(program))
                    {
                        responder.SetError(CreateProtocolExceptionAndLogTelemetry(telemetryEventName, 1009, "attach: property 'program' is missing or empty"));
                        return;
                    }

                    if (!ValidateProgramPath(ref program))
                    {
                        responder.SetError(CreateProtocolExceptionAndLogTelemetry(telemetryEventName, 1010, String.Format(CultureInfo.CurrentCulture, "attach: program path '{0}' does not exist", program)));
                        return;
                    }

                    bool debugServerUsed = false;
                    bool isOpenOCD = false;
                    bool stopAtEntrypoint = false;
                    launchOptions = MILaunchOptions.CreateLaunchOptions(
                        program: program,
                        workingDirectory: string.Empty,
                        args: JsonConvert.SerializeObject(responder.Arguments.ConfigurationProperties),
                        isPipeLaunch: responder.Arguments.ConfigurationProperties.ContainsKey("pipeTransport"),
                        stopAtEntry: out stopAtEntrypoint,
                        isCoreDump: out m_isCoreDump,
                        debugServerUsed: out debugServerUsed,
                        isOpenOCD: out isOpenOCD,
                        visualizerFileUsed: out visualizerFileUsed);
                    executable = program;
                    m_isAttach = true;
                }

                m_processName = program ?? string.Empty;

                // attach
                int hr = m_engineLaunch.LaunchSuspended(null, m_port, executable, null, null, null, launchOptions, 0, 0, 0, 0, this, out m_process);

                if (hr != HRConstants.S_OK)
                {
                    // If the engine raised a message via an error event, fire that instead
                    if (hr == HRConstants.E_ABORT)
                    {
                        string message;
                        lock (m_lock)
                        {
                            message = m_currentLaunchState?.CurrentError?.Item2;
                            m_currentLaunchState = null;
                        }
                        if (message != null)
                        {
                            responder.SetError(CreateProtocolExceptionAndLogTelemetry(telemetryEventName, 1012, message));
                            return;
                        }
                    }

                    eb.ThrowHR(hr);
                }

                hr = m_engineLaunch.ResumeProcess(m_process);
                if (hr < 0)
                {
                    // try to terminate the process if we can
                    try
                    {
                        m_engineLaunch.TerminateProcess(m_process);
                    }
                    catch
                    {
                        // Ignore failures since we are already dealing with an error
                    }

                    eb.ThrowHR(hr);
                }

                var properties = new Dictionary<string, object>(StringComparer.Ordinal);
                properties.Add(DebuggerTelemetry.TelemetryMIMode, mimode);
                properties.Add(DebuggerTelemetry.TelemetryVisualizerFileUsed, visualizerFileUsed);
                properties.Add(DebuggerTelemetry.TelemetrySourceFileMappings, sourceFileMappings);

                DebuggerTelemetry.ReportTimedEvent(telemetryEventName, DateTime.Now - attachStartTime, properties);
                success = true;

                responder.SetResponse(new AttachResponse());
            }
            catch (Exception e)
            {
                responder.SetError(CreateProtocolExceptionAndLogTelemetry(telemetryEventName, 1007, string.Format(CultureInfo.CurrentCulture, AD7Resources.Error_ExceptionOccured, e.InnerException?.ToString() ?? e.ToString())));
            }
            finally
            {
                // Clear _currentLaunchState
                CurrentLaunchState currentLaunchState;
                lock (m_lock)
                {
                    currentLaunchState = m_currentLaunchState;
                    m_currentLaunchState = null;
                }

                // If we had an error event that we didn't wind up returning as an exception, raise it as an event
                Tuple<MessagePrefix, string> currentError = currentLaunchState?.CurrentError;
                if (currentError != null)
                {
                    SendMessageEvent(currentError.Item1, currentError.Item2);
                }

                if (!success)
                {
                    m_process = null;
                    this.Protocol.Stop();
                }
            }
        }

        protected override void HandleDisconnectRequestAsync(IRequestResponder<DisconnectArguments> responder)
        {
            int hr;

            // If we are waiting to continue program create, stop waiting
            m_configurationDoneTCS.TrySetResult(null);

            if (m_process != null)
            {
                string errorReason = null;
                try
                {
                    // Detach if it is attach or TerminateDebuggee is set to false
                    bool shouldDetach = m_isAttach || !responder.Arguments.TerminateDebuggee.GetValueOrDefault(true);
                    hr = shouldDetach ? m_program.Detach() : m_engineLaunch.TerminateProcess(m_process);

                    if (hr < 0)
                    {
                        errorReason = ErrorBuilder.GetErrorDescription(hr);
                    }
                    else
                    {
                        // wait for termination event
                        if (!m_disconnectedOrTerminated.WaitOne(Constants.DisconnectTimeout))
                        {
                            errorReason = AD7Resources.MissingDebuggerTerminationEvent;
                        }
                    }
                }
                catch (Exception e)
                {
                    errorReason = Utilities.GetExceptionDescription(e);
                }

                // VS Code ignores the result of Disconnect. So send an output event instead.
                if (errorReason != null)
                {
                    string message = string.Format(CultureInfo.CurrentCulture, AD7Resources.Warning_Scenario_TerminateProcess, m_processName, errorReason);
                    m_logger.WriteLine(LoggingCategory.DebuggerError, message);
                }
            }

            responder.SetResponse(new DisconnectResponse());

            // Disconnect should terminate protocol
            this.Protocol.Stop();
        }

        protected override void HandleConfigurationDoneRequestAsync(IRequestResponder<ConfigurationDoneArguments> responder)
        {
            // If we are waiting to continue program create, mark that we have now finished initializing settings
            m_configurationDoneTCS.TrySetResult(null);

            responder.SetResponse(new ConfigurationDoneResponse());
        }

        protected override void HandleNextRequestAsync(IRequestResponder<NextArguments> responder)
        {
            try
            {
                StepInternal(responder.Arguments.ThreadId, enum_STEPKIND.STEP_OVER, enum_STEPUNIT.STEP_STATEMENT, AD7Resources.Error_Scenario_Step_Next);
                responder.SetResponse(new NextResponse());
            }
            catch (AD7Exception e)
            {
                responder.SetError(new ProtocolException(e.Message));
            }
        }

        protected override void HandleContinueRequestAsync(IRequestResponder<ContinueArguments, ContinueResponse> responder)
        {
            int threadId = responder.Arguments.ThreadId;

            // Sometimes we can get a threadId of 0. Make sure we don't look it up in this case, otherwise we will crash.
            IDebugThread2 thread = null;
            lock (m_threads)
            {
                if (threadId != 0 && !m_threads.TryGetValue(threadId, out thread))
                {
                    // We do not accept nonzero unknown threadIds.
                    Debug.Fail("Unknown threadId passed to Continue!");
                    return;
                }
            }

            BeforeContinue();
            ErrorBuilder builder = new ErrorBuilder(() => AD7Resources.Error_Scenario_Continue);

            bool succeeded = false;
            try
            {
                builder.CheckHR(m_program.Continue(thread));
                succeeded = true;
                responder.SetResponse(new ContinueResponse());
            }
            catch (AD7Exception e)
            {
                responder.SetError(new ProtocolException(e.Message));
            }
            finally
            {
                if (!succeeded)
                {
                    m_isStopped = true;
                }
            }
        }

        protected override void HandleStepInRequestAsync(IRequestResponder<StepInArguments> responder)
        {
            StepInResponse response = new StepInResponse();

            try
            {
                StepInternal(responder.Arguments.ThreadId, enum_STEPKIND.STEP_INTO, enum_STEPUNIT.STEP_STATEMENT, AD7Resources.Error_Scenario_Step_In);
                responder.SetResponse(response);
            }
            catch (AD7Exception e)
            {
                if (m_isCoreDump)
                {
                    responder.SetError(new ProtocolException(e.Message));
                }
            }
        }

        protected override void HandleStepOutRequestAsync(IRequestResponder<StepOutArguments> responder)
        {
            try
            {
                StepInternal(responder.Arguments.ThreadId, enum_STEPKIND.STEP_OUT, enum_STEPUNIT.STEP_STATEMENT, AD7Resources.Error_Scenario_Step_Out);
                responder.SetResponse(new StepOutResponse());
            }
            catch (AD7Exception e)
            {
                responder.SetError(new ProtocolException(e.Message));
            }
        }

        protected override void HandlePauseRequestAsync(IRequestResponder<PauseArguments> responder)
        {
            // TODO: wait for break event
            m_program.CauseBreak();
            responder.SetResponse(new PauseResponse());
        }

        protected override void HandleStackTraceRequestAsync(IRequestResponder<StackTraceArguments, StackTraceResponse> responder)
        {
            int threadReference = responder.Arguments.ThreadId;
            int startFrame = responder.Arguments.StartFrame.GetValueOrDefault(0);
            int levels = responder.Arguments.Levels.GetValueOrDefault(0);

            StackTraceResponse response = new StackTraceResponse()
            {
                TotalFrames = 0
            };

            // Make sure we are stopped and receiving valid input or else return an empty stack trace
            if (m_isStopped && startFrame >= 0 && levels >= 0)
            {
                ThreadFrameEnumInfo frameEnumInfo;
                if (!m_threadFrameEnumInfos.TryGetValue(threadReference, out frameEnumInfo))
                {
                    IDebugThread2 thread;
                    lock (m_threads)
                    {
                        if (m_threads.TryGetValue(threadReference, out thread))
                        {
                            var flags = enum_FRAMEINFO_FLAGS.FIF_FRAME |   // need a frame object
                                enum_FRAMEINFO_FLAGS.FIF_FUNCNAME |        // need a function name
                                enum_FRAMEINFO_FLAGS.FIF_FUNCNAME_MODULE | // with the module specified
                                enum_FRAMEINFO_FLAGS.FIF_FUNCNAME_ARGS |   // with argument names and types
                                enum_FRAMEINFO_FLAGS.FIF_FUNCNAME_ARGS_TYPES |
                                enum_FRAMEINFO_FLAGS.FIF_FUNCNAME_ARGS_NAMES |
                                enum_FRAMEINFO_FLAGS.FIF_FLAGS;

                            IEnumDebugFrameInfo2 frameEnum;
                            thread.EnumFrameInfo(flags, Constants.EvaluationRadix, out frameEnum);
                            uint totalFrames;
                            frameEnum.GetCount(out totalFrames);

                            frameEnumInfo = new ThreadFrameEnumInfo(frameEnum, totalFrames);
                            m_threadFrameEnumInfos.Add(threadReference, frameEnumInfo);
                        }
                    }
                }

                if (startFrame < frameEnumInfo.TotalFrames)
                {
                    if (startFrame != frameEnumInfo.CurrentPosition)
                    {
                        frameEnumInfo.FrameEnum.Reset();
                        frameEnumInfo.CurrentPosition = (uint)startFrame;

                        if (startFrame > 0)
                        {
                            frameEnumInfo.FrameEnum.Skip((uint)startFrame);
                        }
                    }

                    if (levels == 0)
                    {
                        // take the rest of the stack frames
                        levels = (int)frameEnumInfo.TotalFrames - startFrame;
                    }
                    else
                    {
                        levels = Math.Min((int)frameEnumInfo.TotalFrames - startFrame, levels);
                    }

                    FRAMEINFO[] frameInfoArray = new FRAMEINFO[levels];
                    uint framesFetched = 0;
                    frameEnumInfo.FrameEnum.Next((uint)frameInfoArray.Length, frameInfoArray, ref framesFetched);
                    frameEnumInfo.CurrentPosition += framesFetched;

                    for (int i = 0; i < framesFetched; i++)
                    {
                        // TODO: annotated frames?
                        var frameInfo = frameInfoArray[i];
                        IDebugStackFrame2 frame = frameInfo.m_pFrame;

                        int frameReference = 0;
                        TextPositionTuple textPosition = TextPositionTuple.Nil;

                        if (frame != null)
                        {
                            frameReference = m_frameHandles.Create(frame);
                            textPosition = TextPositionTuple.GetTextPositionOfFrame(m_pathConverter, frame) ?? TextPositionTuple.Nil;
                        }

                        response.StackFrames.Add(new ProtocolMessages.StackFrame()
                        {
                            Id = frameReference,
                            Name = frameInfo.m_bstrFuncName,
                            Source = textPosition.Source,
                            Line = textPosition.Line,
                            Column = textPosition.Column
                        });
                    }

                    response.TotalFrames = (int)frameEnumInfo.TotalFrames;
                }
            }

            responder.SetResponse(response);
        }

        protected override void HandleScopesRequestAsync(IRequestResponder<ScopesArguments, ScopesResponse> responder)
        {
            int frameReference = responder.Arguments.FrameId;
            ScopesResponse response = new ScopesResponse();

            // if we are not stopped return empty scopes
            if (!m_isStopped)
            {
                responder.SetError(new ProtocolException(AD7Resources.Error_TargetNotStopped, new Message(1105, AD7Resources.Error_TargetNotStopped)));
                return;
            }

            IDebugStackFrame2 frame;
            if (m_frameHandles.TryGet(frameReference, out frame))
            {
                uint n;
                IEnumDebugPropertyInfo2 varEnum;
                if (frame.EnumProperties(enum_DEBUGPROP_INFO_FLAGS.DEBUGPROP_INFO_PROP, 10, ref s_guidFilterAllLocalsPlusArgs, 0, out n, out varEnum) == HRConstants.S_OK)
                {
                    if (n > 0)
                    {
                        response.Scopes.Add(new Scope()
                        {
                            Name = AD7Resources.Locals_Scope_Name,
                            VariablesReference = m_variableManager.Create(frame),
                            Expensive = false
                        });
                    }
                }
            }

            responder.SetResponse(response);
        }

        protected override void HandleVariablesRequestAsync(IRequestResponder<VariablesArguments, VariablesResponse> responder)
        {
            int reference = responder.Arguments.VariablesReference;
            VariablesResponse response = new VariablesResponse();

            // if we are not stopped return empty variables
            if (!m_isStopped)
            {
                responder.SetError(new ProtocolException(AD7Resources.Error_TargetNotStopped, new Message(1105, AD7Resources.Error_TargetNotStopped)));
                return;
            }

            Object container;
            if (m_variableManager.TryGet(reference, out container))
            {
                if (container is IDebugStackFrame2)
                {
                    response = VariablesFromFrame(container as IDebugStackFrame2);
                }
                else
                {
                    if (container is VariableEvaluationData)
                    {
                        VariableEvaluationData variableEvaluationData = (VariableEvaluationData)container;
                        IDebugProperty2 property = variableEvaluationData.DebugProperty;

                        Guid empty = Guid.Empty;
                        IEnumDebugPropertyInfo2 childEnum;
                        if (property.EnumChildren(variableEvaluationData.propertyInfoFlags, Constants.EvaluationRadix, ref empty, enum_DBG_ATTRIB_FLAGS.DBG_ATTRIB_ALL, null, Constants.EvaluationTimeout, out childEnum) == 0)
                        {
                            uint count;
                            childEnum.GetCount(out count);
                            if (count > 0)
                            {
                                DEBUG_PROPERTY_INFO[] childProperties = new DEBUG_PROPERTY_INFO[count];
                                childEnum.Next(count, childProperties, out count);

                                if (count > 1)
                                {
                                    // Ensure that items with duplicate names such as multiple anonymous unions will display in VS Code
                                    Dictionary<string, Variable> variablesDictionary = new Dictionary<string, Variable>();
                                    for (uint c = 0; c < count; c++)
                                    {
                                        var variable = m_variableManager.CreateVariable(ref childProperties[c], variableEvaluationData.propertyInfoFlags);
                                        int uniqueCounter = 2;
                                        string variableName = variable.Name;
                                        string variableNameFormat = "{0} #{1}";
                                        while (variablesDictionary.ContainsKey(variableName))
                                        {
                                            variableName = String.Format(CultureInfo.InvariantCulture, variableNameFormat, variable.Name, uniqueCounter++);
                                        }

                                        variable.Name = variableName;
                                        variablesDictionary[variableName] = variable;
                                    }

                                    response.Variables.AddRange(variablesDictionary.Values);
                                }
                                else
                                {
                                    // Shortcut when no duplicate can exist
                                    response.Variables.Add(m_variableManager.CreateVariable(ref childProperties[0], variableEvaluationData.propertyInfoFlags));
                                }
                            }
                        }
                    }
                    else
                    {
                        Debug.Assert(false, "Unexpected type in _variableHandles collection");
                    }
                }
            }

            responder.SetResponse(response);
        }

        protected override void HandleSetVariableRequestAsync(IRequestResponder<SetVariableArguments, SetVariableResponse> responder)
        {
            string name = responder.Arguments.Name;
            string value = responder.Arguments.Value;
            int reference = responder.Arguments.VariablesReference;

            // if we are not stopped don't try to set
            if (!m_isStopped)
            {
                responder.SetError(new ProtocolException(AD7Resources.Error_TargetNotStopped, new Message(1105, AD7Resources.Error_TargetNotStopped)));
                return;
            }

            object container;
            if (!m_variableManager.TryGet(reference, out container))
            {
                responder.SetError(new ProtocolException(AD7Resources.Error_VariableNotFound, new Message(1106, AD7Resources.Error_VariableNotFound)));
                return;
            }

            enum_DEBUGPROP_INFO_FLAGS flags = GetDefaultPropertyInfoFlags();
            IDebugProperty2 property = null;
            IEnumDebugPropertyInfo2 varEnum = null;
            int hr = HRConstants.E_FAIL;
            if (container is IDebugStackFrame2)
            {
                uint n;
                hr = ((IDebugStackFrame2)container).EnumProperties(
                    enum_DEBUGPROP_INFO_FLAGS.DEBUGPROP_INFO_PROP,
                    Constants.EvaluationRadix,
                    ref s_guidFilterAllLocalsPlusArgs,
                    Constants.EvaluationTimeout,
                    out n,
                    out varEnum);
            }
            else if (container is VariableEvaluationData)
            {
                IDebugProperty2 debugProperty = ((VariableEvaluationData)container).DebugProperty;
                if (debugProperty == null)
                {
                    responder.SetError(new ProtocolException(AD7Resources.Error_VariableNotFound, new Message(1106, AD7Resources.Error_VariableNotFound)));
                    return;
                }

                hr = debugProperty.EnumChildren(
                    enum_DEBUGPROP_INFO_FLAGS.DEBUGPROP_INFO_PROP,
                    Constants.EvaluationRadix,
                    ref s_guidFilterAllLocalsPlusArgs,
                    enum_DBG_ATTRIB_FLAGS.DBG_ATTRIB_ALL,
                    name,
                    Constants.EvaluationTimeout,
                    out varEnum);
            }

            if (hr == HRConstants.S_OK && varEnum != null)
            {
                DEBUG_PROPERTY_INFO[] props = new DEBUG_PROPERTY_INFO[1];
                uint nProps;
                while (varEnum.Next(1, props, out nProps) == HRConstants.S_OK)
                {
                    DEBUG_PROPERTY_INFO[] propertyInfo = new DEBUG_PROPERTY_INFO[1];
                    props[0].pProperty.GetPropertyInfo(flags, Constants.EvaluationRadix, Constants.EvaluationTimeout, null, 0, propertyInfo);

                    if (propertyInfo[0].bstrName == name)
                    {
                        // Make sure we can assign to this variable.
                        if (propertyInfo[0].dwAttrib.HasFlag(enum_DBG_ATTRIB_FLAGS.DBG_ATTRIB_VALUE_READONLY))
                        {
                            string message = string.Format(CultureInfo.CurrentCulture, AD7Resources.Error_VariableIsReadonly, name);
                            responder.SetError(new ProtocolException(message, new Message(1107, message)));
                            return;
                        }

                        property = propertyInfo[0].pProperty;
                        break;
                    }
                }
            }

            if (property == null)
            {
                responder.SetError(new ProtocolException(AD7Resources.Error_VariableNotFound, new Message(1106, AD7Resources.Error_VariableNotFound)));
                return;
            }

            string error = null;
            if (property is IDebugProperty3)
            {
                hr = ((IDebugProperty3)property).SetValueAsStringWithError(value, Constants.EvaluationRadix, Constants.EvaluationTimeout, out error);
            }
            else
            {
                hr = property.SetValueAsString(value, Constants.EvaluationRadix, Constants.EvaluationTimeout);
            }

            if (hr != HRConstants.S_OK)
            {
                string message = error ?? AD7Resources.Error_SetVariableFailed;
                responder.SetError(new ProtocolException(message, new Message(1107, message)));
                return;
            }

            responder.SetResponse(new SetVariableResponse
            {
                Value = m_variableManager.CreateVariable(property, flags).Value
            });
        }

        /// <summary>
        /// Currently unsupported. This message can be received when we return a source file that doesn't exist (such as a library within gdb).
        /// See github issue: microsoft/vscode-cpptools#3662
        /// </summary>
        protected override void HandleSourceRequestAsync(IRequestResponder<SourceArguments, SourceResponse> responder)
        {
            responder.SetError(new ProtocolException("'SourceRequest' not supported."));
        }

        protected override void HandleThreadsRequestAsync(IRequestResponder<ThreadsArguments, ThreadsResponse> responder)
        {
            ThreadsResponse response = new ThreadsResponse();

            // Make a copy of the threads list
            Dictionary<int, IDebugThread2> threads;
            lock (m_threads)
            {
                threads = new Dictionary<int, IDebugThread2>(m_threads);
            }

            // iterate over the collection asking the engine for the name
            foreach (var pair in threads)
            {
                string name;
                pair.Value.GetName(out name);
                response.Threads.Add(new OpenDebugThread(pair.Key, name));
            }

            responder.SetResponse(response);
        }

        protected override void HandleSetBreakpointsRequestAsync(IRequestResponder<SetBreakpointsArguments, SetBreakpointsResponse> responder)
        {
            SetBreakpointsResponse response = new SetBreakpointsResponse();

            string path = null;
            string name = null;

            if (responder.Arguments.Source != null)
            {
                string p = responder.Arguments.Source.Path;
                if (p != null && p.Trim().Length > 0)
                {
                    path = p;
                }
                string nm = responder.Arguments.Source.Name;
                if (nm != null && nm.Trim().Length > 0)
                {
                    name = nm;
                }
            }

            var source = new Source()
            {
                Name = name,
                Path = path,
                SourceReference = 0
            };

            List<SourceBreakpoint> breakpoints = responder.Arguments.Breakpoints;

            bool sourceModified = responder.Arguments.SourceModified.GetValueOrDefault(false);

            // we do not support other sources than 'path'
            if (source.Path != null)
            {
                ErrorBuilder eb = new ErrorBuilder(() => AD7Resources.Error_UnableToSetBreakpoint);

                try
                {
                    string convertedPath = m_pathConverter.ConvertClientPathToDebugger(source.Path);

                    if (Utilities.IsWindows() && convertedPath.Length > 2)
                    {
                        // vscode may send drive letters with inconsistent casing which will mess up the key
                        // in the dictionary.  see https://github.com/Microsoft/vscode/issues/6268
                        // Normalize the drive letter casing. Note that drive letters
                        // are not localized so invariant is safe here.
                        string drive = convertedPath.Substring(0, 2);
                        if (char.IsLower(drive[0]) && drive.EndsWith(":", StringComparison.Ordinal))
                        {
                            convertedPath = String.Concat(drive.ToUpperInvariant(), convertedPath.Substring(2));
                        }
                    }

                    HashSet<int> lines = new HashSet<int>(breakpoints.Select((b) => b.Line));

                    Dictionary<int, IDebugPendingBreakpoint2> dict = null;
                    if (m_breakpoints.ContainsKey(convertedPath))
                    {
                        dict = m_breakpoints[convertedPath];
                        var keys = new int[dict.Keys.Count];
                        dict.Keys.CopyTo(keys, 0);
                        foreach (var l in keys)
                        {
                            // Delete all breakpoints that are no longer listed.
                            // In the case of modified source, delete everything.
                            if (!lines.Contains(l) || sourceModified)
                            {
                                var bp = dict[l];
                                bp.Delete();
                                dict.Remove(l);
                            }
                        }
                    }
                    else
                    {
                        dict = new Dictionary<int, IDebugPendingBreakpoint2>();
                        m_breakpoints[convertedPath] = dict;
                    }

                    var resBreakpoints = new List<Breakpoint>();
                    foreach (var bp in breakpoints)
                    {
                        if (dict.ContainsKey(bp.Line))
                        {
                            // already created
                            IDebugBreakpointRequest2 breakpointRequest;
                            if (dict[bp.Line].GetBreakpointRequest(out breakpointRequest) == 0 && 
                                breakpointRequest is AD7BreakPointRequest ad7BPRequest)
                            {
                                // Check to see if this breakpoint has a condition that has changed.
                                if (!StringComparer.Ordinal.Equals(ad7BPRequest.Condition, bp.Condition))
                                {
                                    // Condition has been modified. Delete breakpoint so it will be recreated with the updated condition.
                                    var toRemove = dict[bp.Line];
                                    toRemove.Delete();
                                    dict.Remove(bp.Line);
                                }
                                // Check to see if tracepoint changed
                                else if (!StringComparer.Ordinal.Equals(ad7BPRequest.LogMessage, bp.LogMessage))
                                {
                                    ad7BPRequest.ClearTracepoint();
                                    var toRemove = dict[bp.Line];
                                    toRemove.Delete();
                                    dict.Remove(bp.Line);
                                }
                                else
                                {
                                    if (ad7BPRequest.BindResult != null)
                                    {
                                        // use the breakpoint created from IDebugBreakpointErrorEvent2 or IDebugBreakpointBoundEvent2
                                        resBreakpoints.Add(ad7BPRequest.BindResult);
                                    }
                                    else
                                    {
                                        resBreakpoints.Add(new Breakpoint()
                                        {
                                            Id = (int)ad7BPRequest.Id,
                                            Verified = true,
                                            Line = bp.Line
                                        });
                                    }
                                    continue;
                                }
                            }
                        }


                        // Create a new breakpoint
                        if (!dict.ContainsKey(bp.Line))
                        {
                            IDebugPendingBreakpoint2 pendingBp;
                            AD7BreakPointRequest pBPRequest = new AD7BreakPointRequest(m_sessionConfig, convertedPath, m_pathConverter.ConvertClientLineToDebugger(bp.Line), bp.Condition);

                            try
                            {
                                bool verified = true;
                                if (!string.IsNullOrEmpty(bp.LogMessage))
                                {
                                    // Make sure tracepoint is valid.
                                    verified = pBPRequest.SetLogMessage(bp.LogMessage);
                                }

                                if (verified)
                                {
                                    eb.CheckHR(m_engine.CreatePendingBreakpoint(pBPRequest, out pendingBp));
                                    eb.CheckHR(pendingBp.Bind());

                                    dict[bp.Line] = pendingBp;

                                    resBreakpoints.Add(new Breakpoint()
                                    {
                                        Id = (int)pBPRequest.Id,
                                        Verified = verified,
                                        Line = bp.Line
                                    });
                                }
                                else
                                {
                                    resBreakpoints.Add(new Breakpoint()
                                    {
                                        Id = (int)pBPRequest.Id,
                                        Verified = verified,
                                        Line = bp.Line,
                                        Message = string.Format(CultureInfo.CurrentCulture, AD7Resources.Error_UnableToParseLogMessage)
                                    });
                                }
                            }
                            catch (Exception e)
                            {
                                e = Utilities.GetInnerMost(e);
                                if (Utilities.IsCorruptingException(e))
                                {
                                    Utilities.ReportException(e);
                                }

                                resBreakpoints.Add(new Breakpoint()
                                {
                                    Id = (int)pBPRequest.Id,
                                    Verified = false,
                                    Line = bp.Line,
                                    Message = eb.GetMessageForException(e)
                                });
                            }
                        }
                    }

                    response.Breakpoints = resBreakpoints;
                }
                catch (Exception e)
                {
                    // If setBreakpoints returns an error vscode aborts launch, so we never want to return an error,
                    // so convert this to failure results

                    e = Utilities.GetInnerMost(e);
                    if (Utilities.IsCorruptingException(e))
                    {
                        Utilities.ReportException(e);
                    }

                    string message = eb.GetMessageForException(e);
                    List<Breakpoint> resBreakpoints = breakpoints.Select(bp => new Breakpoint()
                    {
                        Id = (int)AD7BreakPointRequest.GetNextBreakpointId(),
                        Verified = false,
                        Line = bp.Line,
                        Message = message
                    }).ToList();

                    response.Breakpoints = resBreakpoints;
                }
            }

            responder.SetResponse(response);
        }

        protected override void HandleSetExceptionBreakpointsRequestAsync(IRequestResponder<SetExceptionBreakpointsArguments> responder)
        {
            List<string> filter = responder.Arguments.Filters;
            if (m_engineConfiguration.ExceptionSettings.Categories.Count > 0)
            {
                if (filter == null || filter.Count == 0)
                {
                    SetAllExceptions(enum_EXCEPTION_STATE.EXCEPTION_STOP_SECOND_CHANCE);
                }
                else if (filter.Contains(ExceptionBreakpointFilter.Filter_All))
                {
                    enum_EXCEPTION_STATE state = enum_EXCEPTION_STATE.EXCEPTION_STOP_SECOND_CHANCE | enum_EXCEPTION_STATE.EXCEPTION_STOP_FIRST_CHANCE | enum_EXCEPTION_STATE.EXCEPTION_STOP_USER_FIRST_CHANCE;

                    if (filter.Contains(ExceptionBreakpointFilter.Filter_UserUnhandled))
                    {
                        state |= enum_EXCEPTION_STATE.EXCEPTION_STOP_USER_UNCAUGHT;
                    }

                    SetAllExceptions(state);
                }
                else
                {
                    if (filter.Contains(ExceptionBreakpointFilter.Filter_UserUnhandled))
                    {
                        SetAllExceptions(enum_EXCEPTION_STATE.EXCEPTION_STOP_SECOND_CHANCE | enum_EXCEPTION_STATE.EXCEPTION_STOP_USER_UNCAUGHT);
                    }
                    else
                    {
                        // TODO: once VS Code has UI to break on more than just 'uncaught' and 'all' we will need to enhance this with more features
                        Debug.Fail("Unexpected exception filter string");
                    }
                }
            }

            responder.SetResponse(new SetExceptionBreakpointsResponse());
        }

        protected override void HandleSetFunctionBreakpointsRequestAsync(IRequestResponder<SetFunctionBreakpointsArguments, SetFunctionBreakpointsResponse> responder)
        {
            if (responder.Arguments.Breakpoints == null)
            {
                responder.SetError(new ProtocolException("SetFunctionBreakpointRequest failed: Missing 'breakpoints'."));
                return;
            }

            List<FunctionBreakpoint> breakpoints = responder.Arguments.Breakpoints;
            Dictionary<string, IDebugPendingBreakpoint2> newBreakpoints = new Dictionary<string, IDebugPendingBreakpoint2>();

            SetFunctionBreakpointsResponse response = new SetFunctionBreakpointsResponse();

            foreach (KeyValuePair<string, IDebugPendingBreakpoint2> b in m_functionBreakpoints)
            {
                if (breakpoints.Find((p) => p.Name == b.Key) != null)
                {
                    newBreakpoints[b.Key] = b.Value;    // breakpoint still in new list
                }
                else
                {
                    b.Value.Delete();   // not in new list so delete it
                }
            }

            foreach (FunctionBreakpoint b in breakpoints)
            {
                if (m_functionBreakpoints.ContainsKey(b.Name))
                {   // already created
                    IDebugBreakpointRequest2 breakpointRequest;
                    if (m_functionBreakpoints[b.Name].GetBreakpointRequest(out breakpointRequest) == 0 &&
                                breakpointRequest is AD7BreakPointRequest ad7BPRequest)
                    {
                        // Check to see if this breakpoint has a condition that has changed.
                        if (!StringComparer.Ordinal.Equals(ad7BPRequest.Condition, b.Condition))
                        {
                            // Condition has been modified. Delete breakpoint so it will be recreated with the updated condition.
                            var toRemove = m_functionBreakpoints[b.Name];
                            toRemove.Delete();
                            m_functionBreakpoints.Remove(b.Name);
                        }
                        else
                        {
                            if (ad7BPRequest.BindResult != null)
                            {
                                response.Breakpoints.Add(ad7BPRequest.BindResult);
                            }
                            else
                            {
                                response.Breakpoints.Add(new Breakpoint()
                                {
                                    Id = (int)ad7BPRequest.Id,
                                    Verified = true,
                                    Line = 0
                                });

                            }
                            continue;
                        }
                    }
                }

                // bind the new function names
                if (!m_functionBreakpoints.ContainsKey(b.Name))
                {
                    IDebugPendingBreakpoint2 pendingBp;
                    AD7BreakPointRequest pBPRequest = new AD7BreakPointRequest(b.Name);

                    int hr = m_engine.CreatePendingBreakpoint(pBPRequest, out pendingBp);

                    if (hr == HRConstants.S_OK && pendingBp != null)
                    {
                        hr = pendingBp.Bind();
                    }

                    if (hr == HRConstants.S_OK)
                    {
                        newBreakpoints[b.Name] = pendingBp;
                        response.Breakpoints.Add(new Breakpoint()
                        {
                            Id = (int)pBPRequest.Id,
                            Verified = true,
                            Line = 0
                        }); // success
                    }
                    else
                    {
                        response.Breakpoints.Add(new Breakpoint()
                        {
                            Id = (int)pBPRequest.Id,
                            Verified = false,
                            Line = 0
                        }); // couldn't create and/or bind
                    }
                }
            }

            m_functionBreakpoints = newBreakpoints;

            responder.SetResponse(response);
        }

        protected override void HandleEvaluateRequestAsync(IRequestResponder<EvaluateArguments, EvaluateResponse> responder)
        {
            EvaluateArguments.ContextValue context = responder.Arguments.Context.GetValueOrDefault(EvaluateArguments.ContextValue.Unknown);
            int frameId = responder.Arguments.FrameId.GetValueOrDefault(-1);
            string expression = responder.Arguments.Expression;

            if (expression == null)
            {
                responder.SetError(new ProtocolException("Failed to handle EvaluateRequest: Missing 'expression'"));
                return;
            }

            // if we are not stopped, return evaluation failure
            if (!m_isStopped)
            {
                responder.SetError(new ProtocolException("Failed to handle EvaluateRequest", new Message(1105, AD7Resources.Error_TargetNotStopped)));
                return;
            }
            DateTime evaluationStartTime = DateTime.Now;

            bool isExecInConsole = false;
            // If the expression isn't empty and its a Repl request, do additional checking
            if (!String.IsNullOrEmpty(expression) && context == EvaluateArguments.ContextValue.Repl)
            {
                // If this is an -exec command (or starts with '`') treat it as a console command and log telemetry
                if (expression.StartsWith("-exec", StringComparison.Ordinal) || expression[0] == '`')
                    isExecInConsole = true;
            }

            int hr;
            ErrorBuilder eb = new ErrorBuilder(() => AD7Resources.Error_Scenario_Evaluate);
            IDebugStackFrame2 frame;

            bool success = false;
            if (frameId == -1 && isExecInConsole)
            {
                // If exec in console and no stack frame, evaluate off the top frame.
                success = m_frameHandles.TryGetFirst(out frame);
            }
            else
            {
                success = m_frameHandles.TryGet(frameId, out frame);
            }

            if (!success)
            {
                Dictionary<string, object> properties = new Dictionary<string, object>();
                properties.Add(DebuggerTelemetry.TelemetryStackFrameId, frameId);
                properties.Add(DebuggerTelemetry.TelemetryExecuteInConsole, isExecInConsole);
                DebuggerTelemetry.ReportError(DebuggerTelemetry.TelemetryEvaluateEventName, 1108, "Invalid frameId", properties);
                responder.SetError(new ProtocolException("Cannot evaluate expression on the specified stack frame."));
                return;
            }
            enum_EVALFLAGS flags = 0;
            DAPEvalFlags dapEvalFlags = DAPEvalFlags.NONE;

            if (context == EvaluateArguments.ContextValue.Hover) // No side effects for data tips	
            {
                flags |= enum_EVALFLAGS.EVAL_NOSIDEEFFECTS;
            }

            if (context == EvaluateArguments.ContextValue.Clipboard)
            {
                dapEvalFlags |= DAPEvalFlags.CLIPBOARD_CONTEXT;
            }

            IDebugProperty2 property;
            string error;
            hr = GetIDebugPropertyFromExpression(expression, frame, flags, dapEvalFlags, out property, out error);
            if (!string.IsNullOrEmpty(error))
            {
                responder.SetError(new ProtocolException(error));
                return;
            }
            eb.CheckHR(hr);
            eb.CheckOutput(property);

            DEBUG_PROPERTY_INFO[] propertyInfo = new DEBUG_PROPERTY_INFO[1];
            enum_DEBUGPROP_INFO_FLAGS propertyInfoFlags = GetDefaultPropertyInfoFlags();

            if (context == EvaluateArguments.ContextValue.Hover) // No side effects for data tips
            {
                propertyInfoFlags |= (enum_DEBUGPROP_INFO_FLAGS)enum_DEBUGPROP_INFO_FLAGS110.DEBUGPROP110_INFO_NOSIDEEFFECTS;
            }

            property.GetPropertyInfo(propertyInfoFlags, Constants.EvaluationRadix, Constants.EvaluationTimeout, null, 0, propertyInfo);

            // If the expression evaluation produces an error result and we are trying to get the expression for data tips
            // return a failure result so that VS code won't display the error message in data tips
            if (((propertyInfo[0].dwAttrib & enum_DBG_ATTRIB_FLAGS.DBG_ATTRIB_VALUE_ERROR) == enum_DBG_ATTRIB_FLAGS.DBG_ATTRIB_VALUE_ERROR) && context == EvaluateArguments.ContextValue.Hover)
            {
                responder.SetError(new ProtocolException("Evaluation error"));
                return;
            }

            Variable variable = m_variableManager.CreateVariable(ref propertyInfo[0], propertyInfoFlags);

            if (context != EvaluateArguments.ContextValue.Hover)
            {
                DebuggerTelemetry.ReportEvaluation(
                    ((propertyInfo[0].dwAttrib & enum_DBG_ATTRIB_FLAGS.DBG_ATTRIB_VALUE_ERROR) == enum_DBG_ATTRIB_FLAGS.DBG_ATTRIB_VALUE_ERROR),
                    DateTime.Now - evaluationStartTime,
                    isExecInConsole ? new Dictionary<string, object>() { { DebuggerTelemetry.TelemetryExecuteInConsole, true } } : null);
            }

            responder.SetResponse(new EvaluateResponse()
            {
                Result = variable.Value,
                Type = variable.Type,
                VariablesReference = variable.VariablesReference
            });

        }

        protected override void HandleReadMemoryRequestAsync(IRequestResponder<ReadMemoryArguments, ReadMemoryResponse> responder)
        {
            int hr;
            ReadMemoryArguments rma = responder.Arguments;
            ErrorBuilder eb = new ErrorBuilder(() => AD7Resources.Error_Scenario_ReadMemory);
            try
            {
                IDebugStackFrame2 frame;
                m_frameHandles.TryGetFirst(out frame);
                eb.CheckOutput(frame);

                IDebugProperty2 property;
                string error;
                hr = GetIDebugPropertyFromExpression(rma.MemoryReference, frame, 0, DAPEvalFlags.NONE, out property, out error);
                if (!string.IsNullOrEmpty(error))
                {
                    responder.SetError(new ProtocolException(error));
                    return;
                }
                eb.CheckHR(hr);
                eb.CheckOutput(property);

                hr = property.GetMemoryContext(out IDebugMemoryContext2 ppMemory);
                eb.CheckHR(hr);

                if (rma.Offset.HasValue)
                {
                    if (rma.Offset > 0)
                    {
                        hr = ppMemory.Add((ulong)rma.Offset, out IDebugMemoryContext2 ppMemCxt);
                    }
                    else
                    {
                        ulong offset = (ulong)-rma.Offset;
                        hr = ppMemory.Subtract(offset, out IDebugMemoryContext2 ppMemCxt);
                    }
                    eb.CheckHR(hr);
                }

                byte[] data = new byte[rma.Count];
                uint pdwUnreadable = 0;
                hr = m_program.GetMemoryBytes(out IDebugMemoryBytes2 debugMemoryBytes);
                eb.CheckHR(hr);
                hr = debugMemoryBytes.ReadAt(ppMemory, (uint)rma.Count, data, out uint pdwRead, ref pdwUnreadable);
                eb.CheckHR(hr);

                responder.SetResponse(new ReadMemoryResponse()
                {
                    Address = rma.MemoryReference,
                    Data = Convert.ToBase64String(data),
                    UnreadableBytes = (int?)pdwUnreadable
                });
            }
            catch (AD7Exception e)
            {
                responder.SetError(new ProtocolException(e.Message));
            }
        }

        #endregion

        #region IDebugPortNotify2

        int IDebugPortNotify2.AddProgramNode(IDebugProgramNode2 programNode)
        {
            if (m_process == null || m_engine == null)
            {
                throw new InvalidOperationException();
            }

            IDebugProgram2[] programs = { new AD7Program(m_process) };
            IDebugProgramNode2[] programNodes = { programNode };

            return m_engine.Attach(programs, programNodes, 1, this, enum_ATTACH_REASON.ATTACH_REASON_LAUNCH);
        }

        int IDebugPortNotify2.RemoveProgramNode(IDebugProgramNode2 pProgramNode)
        {
            return HRConstants.S_OK;
        }

        #endregion

        #region IDebugEventCallback2

        private readonly Dictionary<Guid, Action<IDebugEngine2, IDebugProcess2, IDebugProgram2, IDebugThread2, IDebugEvent2>> m_syncEventHandler = new Dictionary<Guid, Action<IDebugEngine2, IDebugProcess2, IDebugProgram2, IDebugThread2, IDebugEvent2>>();
        private readonly Dictionary<Guid, Func<IDebugEngine2, IDebugProcess2, IDebugProgram2, IDebugThread2, IDebugEvent2, Task>> m_asyncEventHandler = new Dictionary<Guid, Func<IDebugEngine2, IDebugProcess2, IDebugProgram2, IDebugThread2, IDebugEvent2, Task>>();

        private void RegisterAD7EventCallbacks()
        {
            // Sync Handlers
            RegisterSyncEventHandler(typeof(IDebugEngineCreateEvent2), HandleIDebugEngineCreateEvent2);
            RegisterSyncEventHandler(typeof(IDebugStepCompleteEvent2), HandleIDebugStepCompleteEvent2);
            RegisterSyncEventHandler(typeof(IDebugEntryPointEvent2), HandleIDebugEntryPointEvent2);
            RegisterSyncEventHandler(typeof(IDebugBreakpointEvent2), HandleIDebugBreakpointEvent2);
            RegisterSyncEventHandler(typeof(IDebugBreakEvent2), HandleIDebugBreakEvent2);
            RegisterSyncEventHandler(typeof(IDebugExceptionEvent2), HandleIDebugExceptionEvent2);
            RegisterSyncEventHandler(typeof(IDebugProgramDestroyEvent2), HandleIDebugProgramDestroyEvent2);
            RegisterSyncEventHandler(typeof(IDebugThreadCreateEvent2), HandleIDebugThreadCreateEvent2);
            RegisterSyncEventHandler(typeof(IDebugThreadDestroyEvent2), HandleIDebugThreadDestroyEvent2);
            RegisterSyncEventHandler(typeof(IDebugModuleLoadEvent2), HandleIDebugModuleLoadEvent2);
            RegisterSyncEventHandler(typeof(IDebugBreakpointBoundEvent2), HandleIDebugBreakpointBoundEvent2);
            RegisterSyncEventHandler(typeof(IDebugBreakpointErrorEvent2), HandleIDebugBreakpointErrorEvent2);
            RegisterSyncEventHandler(typeof(IDebugOutputStringEvent2), HandleIDebugOutputStringEvent2);
            RegisterSyncEventHandler(typeof(IDebugMessageEvent2), HandleIDebugMessageEvent2);

            // Async Handlers
            RegisterAsyncEventHandler(typeof(IDebugProgramCreateEvent2), HandleIDebugProgramCreateEvent2);
        }

        private void RegisterSyncEventHandler(Type type, Action<IDebugEngine2, IDebugProcess2, IDebugProgram2, IDebugThread2, IDebugEvent2> handler)
        {
            m_syncEventHandler.Add(type.GetTypeInfo().GUID, handler);
        }

        private void RegisterAsyncEventHandler(Type type, Func<IDebugEngine2, IDebugProcess2, IDebugProgram2, IDebugThread2, IDebugEvent2, Task> handler)
        {
            m_asyncEventHandler.Add(type.GetTypeInfo().GUID, handler);
        }

        public int Event(IDebugEngine2 pEngine, IDebugProcess2 pProcess, IDebugProgram2 pProgram, IDebugThread2 pThread, IDebugEvent2 pEvent, ref Guid riidEvent, uint dwAttrib)
        {
            enum_EVENTATTRIBUTES attributes = unchecked((enum_EVENTATTRIBUTES)dwAttrib);

            Action<IDebugEngine2, IDebugProcess2, IDebugProgram2, IDebugThread2, IDebugEvent2> syncEventHandler;
            if (m_syncEventHandler.TryGetValue(riidEvent, out syncEventHandler))
            {
                syncEventHandler(pEngine, pProcess, pProgram, pThread, pEvent);
            }

            Task task = null;
            Func<IDebugEngine2, IDebugProcess2, IDebugProgram2, IDebugThread2, IDebugEvent2, Task> asyncEventHandler;
            if (m_asyncEventHandler.TryGetValue(riidEvent, out asyncEventHandler))
            {
                task = asyncEventHandler(pEngine, pProcess, pProgram, pThread, pEvent);
            }

            if (attributes.HasFlag(enum_EVENTATTRIBUTES.EVENT_SYNCHRONOUS))
            {
                if (task == null)
                {
                    pEngine.ContinueFromSynchronousEvent(pEvent);
                }
                else
                {
                    task.ContinueWith((j) => pEngine.ContinueFromSynchronousEvent(pEvent));
                }
            }

            return 0;
        }

        public void HandleIDebugEngineCreateEvent2(IDebugEngine2 pEngine, IDebugProcess2 pProcess, IDebugProgram2 pProgram, IDebugThread2 pThread, IDebugEvent2 pEvent)
        {
            // Send configuration settings (e.g. Just My Code) to the engine.
            m_engine.SetMetric("JustMyCodeStepping", m_sessionConfig.JustMyCode ? "1" : "0");
            m_engine.SetMetric("EnableStepFiltering", m_sessionConfig.EnableStepFiltering ? "1" : "0");
        }

        public void HandleIDebugStepCompleteEvent2(IDebugEngine2 pEngine, IDebugProcess2 pProcess, IDebugProgram2 pProgram, IDebugThread2 pThread, IDebugEvent2 pEvent)
        {
            FireStoppedEvent(pThread, StoppedEvent.ReasonValue.Step);
        }

        public void HandleIDebugEntryPointEvent2(IDebugEngine2 pEngine, IDebugProcess2 pProcess, IDebugProgram2 pProgram, IDebugThread2 pThread, IDebugEvent2 pEvent)
        {
            if (m_sessionConfig.StopAtEntrypoint)
            {
                FireStoppedEvent(pThread, StoppedEvent.ReasonValue.Step);
            }
            else
            {
                BeforeContinue();
                m_program.Continue(pThread);
            }
        }

        public void HandleIDebugBreakpointEvent2(IDebugEngine2 pEngine, IDebugProcess2 pProcess, IDebugProgram2 pProgram, IDebugThread2 pThread, IDebugEvent2 pEvent)
        {
            IList<Tracepoint> tracepoints = GetTracepoints(pEvent as IDebugBreakpointEvent2);
            if (tracepoints.Any())
            {
                ThreadPool.QueueUserWorkItem((o) =>
                {
                    foreach (var tp in tracepoints)
                    {
                        int hr = tp.GetLogMessage(pThread, Constants.ParseRadix, m_processName, out string logMessage);
                        if (hr != HRConstants.S_OK)
                        {
                            DebuggerTelemetry.ReportError(DebuggerTelemetry.TelemetryTracepointEventName, logMessage);
                            m_logger.WriteLine(LoggingCategory.DebuggerError, logMessage);
                        }
                        else
                        {
                            m_logger.WriteLine(LoggingCategory.DebuggerStatus, logMessage);
                        }
                    }

                    // Need to check to see if the previous continuation of the debuggee was a step. 
                    // If so, we need to send a stopping event to the UI to signal the step completed successfully. 
                    if (!m_isStepping)
                    {
                        ThreadPool.QueueUserWorkItem((obj) =>
                        {
                            BeforeContinue();
                            m_program.Continue(pThread);
                        });
                    }
                    else
                    {
                        FireStoppedEvent(pThread, StoppedEvent.ReasonValue.Breakpoint);
                    }
                });
            }
            else
            {
                FireStoppedEvent(pThread, StoppedEvent.ReasonValue.Breakpoint);
            }
        }

        public void HandleIDebugBreakEvent2(IDebugEngine2 pEngine, IDebugProcess2 pProcess, IDebugProgram2 pProgram, IDebugThread2 pThread, IDebugEvent2 pEvent)
        {
            DebuggerTelemetry.ReportEvent(DebuggerTelemetry.TelemetryPauseEventName);
            FireStoppedEvent(pThread, StoppedEvent.ReasonValue.Pause);
        }

        public void HandleIDebugExceptionEvent2(IDebugEngine2 pEngine, IDebugProcess2 pProcess, IDebugProgram2 pProgram, IDebugThread2 pThread, IDebugEvent2 pEvent)
        {
            Stopped(pThread);

            IDebugExceptionEvent2 exceptionEvent = (IDebugExceptionEvent2)pEvent;

            string exceptionDescription;
            exceptionEvent.GetExceptionDescription(out exceptionDescription);

            FireStoppedEvent(pThread, StoppedEvent.ReasonValue.Exception, exceptionDescription);
        }

        public Task HandleIDebugProgramCreateEvent2(IDebugEngine2 pEngine, IDebugProcess2 pProcess, IDebugProgram2 pProgram, IDebugThread2 pThread, IDebugEvent2 pEvent)
        {
            Debug.Assert(m_program == null, "Multiple program create events?");
            if (m_program == null)
            {
                m_program = pProgram;
                Protocol.SendEvent(new InitializedEvent());
            }

            return m_configurationDoneTCS.Task;
        }

        public void HandleIDebugProgramDestroyEvent2(IDebugEngine2 pEngine, IDebugProcess2 pProcess, IDebugProgram2 pProgram, IDebugThread2 pThread, IDebugEvent2 pEvent)
        {
            if (pProcess == null)
            {
                pProcess = m_process;
            }
            m_process = null;

            if (pProcess != null)
            {
                m_port.RemoveProcess(pProcess);
            }

            string exitMessage;
            uint ec = 0;
            if (m_isAttach)
            {
                exitMessage = string.Format(CultureInfo.CurrentCulture, AD7Resources.DebuggerDisconnectMessage, m_processName);
            }
            else
            {
                IDebugProgramDestroyEvent2 ee = (IDebugProgramDestroyEvent2)pEvent;
                ee.GetExitCode(out ec);
                exitMessage = string.Format(CultureInfo.CurrentCulture, AD7Resources.ProcessExitMessage, m_processName, (int)ec);
            }

            m_logger.WriteLine(LoggingCategory.ProcessExit, exitMessage);

            Protocol.SendEvent(new ExitedEvent((int)ec));
            Protocol.SendEvent(new TerminatedEvent());


            SendDebugCompletedTelemetry();
            m_disconnectedOrTerminated.Set();
        }

        public void HandleIDebugThreadCreateEvent2(IDebugEngine2 pEngine, IDebugProcess2 pProcess, IDebugProgram2 pProgram, IDebugThread2 pThread, IDebugEvent2 pEvent)
        {
            int id = pThread.Id();
            lock (m_threads)
            {
                m_threads[id] = pThread;
            }
            Protocol.SendEvent(new ThreadEvent(ThreadEvent.ReasonValue.Started, id));
        }

        public void HandleIDebugThreadDestroyEvent2(IDebugEngine2 pEngine, IDebugProcess2 pProcess, IDebugProgram2 pProgram, IDebugThread2 pThread, IDebugEvent2 pEvent)
        {
            int id = pThread.Id();

            lock (m_threads)
            {
                m_threads.Remove(id);
            }
            Protocol.SendEvent(new ThreadEvent(ThreadEvent.ReasonValue.Exited, id));
        }

        public void HandleIDebugModuleLoadEvent2(IDebugEngine2 pEngine, IDebugProcess2 pProcess, IDebugProgram2 pProgram, IDebugThread2 pThread, IDebugEvent2 pEvent)
        {
            IDebugModule2 module;
            string moduleLoadMessage = null;
            int isLoad = 0;
            ((IDebugModuleLoadEvent2)pEvent).GetModule(out module, ref moduleLoadMessage, ref isLoad);

            m_logger.WriteLine(LoggingCategory.Module, moduleLoadMessage);
        }

        public void HandleIDebugBreakpointBoundEvent2(IDebugEngine2 pEngine, IDebugProcess2 pProcess, IDebugProgram2 pProgram, IDebugThread2 pThread, IDebugEvent2 pEvent)
        {
            var breakpointBoundEvent = (IDebugBreakpointBoundEvent2)pEvent;

            foreach (var boundBreakpoint in GetBoundBreakpoints(breakpointBoundEvent))
            {
                IDebugPendingBreakpoint2 pendingBreakpoint;
                if (boundBreakpoint.GetPendingBreakpoint(out pendingBreakpoint) == HRConstants.S_OK)
                {
                    IDebugBreakpointRequest2 breakpointRequest;
                    if (pendingBreakpoint.GetBreakpointRequest(out breakpointRequest) == HRConstants.S_OK)
                    {
                        AD7BreakPointRequest ad7BPRequest = (AD7BreakPointRequest)breakpointRequest;

                        // Once bound, attempt to get the bound line number from the breakpoint.
                        // If the AD7 calls fail, fallback to the original pending breakpoint line number.
                        int? lineNumber = GetBoundBreakpointLineNumber(boundBreakpoint);
                        if (lineNumber == null && ad7BPRequest.DocumentPosition != null)
                        {
                            lineNumber = m_pathConverter.ConvertDebuggerLineToClient(ad7BPRequest.DocumentPosition.Line);
                        }

                        Breakpoint bp = new Breakpoint()
                        {
                            Verified = true,
                            Id = (int)ad7BPRequest.Id,
                            Line = lineNumber.GetValueOrDefault(0)
                        };

                        ad7BPRequest.BindResult = bp;
                        Protocol.SendEvent(new BreakpointEvent(BreakpointEvent.ReasonValue.Changed, bp));
                    }
                }
            }
        }

        public void HandleIDebugBreakpointErrorEvent2(IDebugEngine2 pEngine, IDebugProcess2 pProcess, IDebugProgram2 pProgram, IDebugThread2 pThread, IDebugEvent2 pEvent)
        {
            var breakpointErrorEvent = (IDebugBreakpointErrorEvent2)pEvent;

            IDebugErrorBreakpoint2 errorBreakpoint;
            if (breakpointErrorEvent.GetErrorBreakpoint(out errorBreakpoint) == 0)
            {
                IDebugPendingBreakpoint2 pendingBreakpoint;
                if (errorBreakpoint.GetPendingBreakpoint(out pendingBreakpoint) == 0)
                {
                    IDebugBreakpointRequest2 breakpointRequest;
                    if (pendingBreakpoint.GetBreakpointRequest(out breakpointRequest) == 0)
                    {
                        string errorMsg = string.Empty;

                        IDebugErrorBreakpointResolution2 errorBreakpointResolution;
                        if (errorBreakpoint.GetBreakpointResolution(out errorBreakpointResolution) == 0)
                        {
                            BP_ERROR_RESOLUTION_INFO[] bpInfo = new BP_ERROR_RESOLUTION_INFO[1];
                            if (errorBreakpointResolution.GetResolutionInfo(enum_BPERESI_FIELDS.BPERESI_MESSAGE, bpInfo) == 0)
                            {
                                errorMsg = bpInfo[0].bstrMessage;
                            }
                        }

                        AD7BreakPointRequest ad7BPRequest = (AD7BreakPointRequest)breakpointRequest;
                        Breakpoint bp = null;
                        if (ad7BPRequest.DocumentPosition != null)
                        {
                            if (string.IsNullOrWhiteSpace(ad7BPRequest.Condition))
                            {
                                bp = new Breakpoint()
                                {
                                    Verified = false,
                                    Id = (int)ad7BPRequest.Id,
                                    Line = m_pathConverter.ConvertDebuggerLineToClient(ad7BPRequest.DocumentPosition.Line),
                                    Message = errorMsg
                                };
                            }
                            else
                            {
                                bp = new Breakpoint()
                                {
                                    Verified = false,
                                    Id = (int)ad7BPRequest.Id,
                                    Line = m_pathConverter.ConvertDebuggerLineToClient(ad7BPRequest.DocumentPosition.Line),
                                    Message = string.Format(CultureInfo.CurrentCulture, AD7Resources.Error_ConditionBreakpoint, ad7BPRequest.Condition, errorMsg)
                                };
                            }
                        }
                        else
                        {
                            bp = new Breakpoint()
                            {
                                Verified = false,
                                Id = (int)ad7BPRequest.Id,
                                Line = 0,
                                Message = errorMsg
                            };

                            // TODO: currently VSCode will ignore the error message from "breakpoint" event, the workaround is to log the error to output window
                            string outputMsg = string.Format(CultureInfo.CurrentCulture, AD7Resources.Error_FunctionBreakpoint, ad7BPRequest.FunctionPosition.Name, errorMsg);
                            m_logger.WriteLine(LoggingCategory.DebuggerError, outputMsg);
                        }

                        ad7BPRequest.BindResult = bp;
                        Protocol.SendEvent(new BreakpointEvent(BreakpointEvent.ReasonValue.Changed, bp));
                    }
                }
            }
        }

        public void HandleIDebugOutputStringEvent2(IDebugEngine2 pEngine, IDebugProcess2 pProcess, IDebugProgram2 pProgram, IDebugThread2 pThread, IDebugEvent2 pEvent)
        {
            // OutputStringEvent will include program output if the external console is disabled.

            var outputStringEvent = (IDebugOutputStringEvent2)pEvent;
            string text;
            if (outputStringEvent.GetString(out text) == 0)
            {
                m_logger.Write(LoggingCategory.StdOut, text);
            }
        }

        public void HandleIDebugMessageEvent2(IDebugEngine2 pEngine, IDebugProcess2 pProcess, IDebugProgram2 pProgram, IDebugThread2 pThread, IDebugEvent2 pEvent)
        {
            var outputStringEvent = (IDebugMessageEvent2)pEvent;
            string text;
            enum_MESSAGETYPE[] messageType = new enum_MESSAGETYPE[1];
            uint type, helpId;
            string helpFileName;
            // TODO: Add VS Code support for message box based events, for now we will just output them
            // to the console since that is the best we can do.
            if (outputStringEvent.GetMessage(messageType, out text, out type, out helpFileName, out helpId) == 0)
            {
                const uint MB_ICONERROR = 0x00000010;
                const uint MB_ICONWARNING = 0x00000030;

                if ((messageType[0] & enum_MESSAGETYPE.MT_TYPE_MASK) == enum_MESSAGETYPE.MT_MESSAGEBOX)
                {
                    MessagePrefix prefix = MessagePrefix.None;
                    uint icon = type & 0xf0;
                    if (icon == MB_ICONERROR)
                    {
                        prefix = MessagePrefix.Error;
                    }
                    else if (icon == MB_ICONWARNING)
                    {
                        prefix = MessagePrefix.Warning;
                    }

                    // If we get an error message event during the launch, save it, as we may well want to return that as the launch failure message back to VS Code.
                    if (m_currentLaunchState != null && prefix != MessagePrefix.None)
                    {
                        lock (m_lock)
                        {
                            if (m_currentLaunchState != null && m_currentLaunchState.CurrentError == null)
                            {
                                m_currentLaunchState.CurrentError = new Tuple<MessagePrefix, string>(prefix, text);
                                return;
                            }
                        }
                    }

                    SendMessageEvent(prefix, text);
                }
                else if ((messageType[0] & enum_MESSAGETYPE.MT_REASON_MASK) == enum_MESSAGETYPE.MT_REASON_EXCEPTION)
                {
                    m_logger.Write(LoggingCategory.Exception, text);
                }
                else
                {
                    LoggingCategory category = LoggingCategory.DebuggerStatus;
                    // Check if the message looks like an error or warning. We will check with whatever
                    // our localized error/warning prefix might be and we will also accept the English
                    // version of the string.
                    if (text.StartsWith(AD7Resources.Prefix_Error, StringComparison.OrdinalIgnoreCase) ||
                        text.StartsWith(AD7Resources.Prefix_Warning, StringComparison.OrdinalIgnoreCase) ||
                        text.StartsWith("Error:", StringComparison.OrdinalIgnoreCase) ||
                        text.StartsWith("Warning:", StringComparison.OrdinalIgnoreCase))
                    {
                        category = LoggingCategory.DebuggerError;
                    }

                    m_logger.Write(category, text);
                }
            }
        }

        #endregion
    }
}
