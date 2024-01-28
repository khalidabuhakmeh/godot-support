using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using JetBrains.Annotations;
using JetBrains.ReSharper.Plugins.Godot.Rider.Debugger.Values;
using JetBrains.Util;
using Mono.Debugger.Soft;
using Mono.Debugging.Autofac;
using Mono.Debugging.Backend.Values;
using Mono.Debugging.Backend.Values.ValueReferences;
using Mono.Debugging.Backend.Values.ValueRoles;
using Mono.Debugging.Client;
using Mono.Debugging.Client.CallStacks;
using Mono.Debugging.Client.Values;
using Mono.Debugging.Client.Values.Render;
using Mono.Debugging.Evaluation;
using Mono.Debugging.Soft;
using Mono.Debugging.Win32;

namespace JetBrains.ReSharper.Plugins.Godot.Rider.Debugger.Evaluation
{
    [DebuggerSessionComponent(typeof(SoftDebuggerType))]
    public class MonoGodotAdditionalValuesProvider : GodotAdditionalValuesProvider<Value>
    {
        public MonoGodotAdditionalValuesProvider(IDebuggerSession session, IValueServicesFacade<Value> valueServices,
                                             IOptions options, ILogger logger)
            : base(session, valueServices, options, logger)
        {
        }
    }
    
    [DebuggerSessionComponent(typeof(CorDebuggerType))]
    public class CorGodotAdditionalValuesProvider : GodotAdditionalValuesProvider<ICorValue>
    {
        public CorGodotAdditionalValuesProvider(IDebuggerSession session, IValueServicesFacade<ICorValue> valueServices,
            IOptions options, ILogger logger)
            : base(session, valueServices, options, logger)
        {
        }
    }

    public class GodotAdditionalValuesProvider<TValue> : IAdditionalValuesProvider
        where TValue : class
    {
        private readonly IDebuggerSession mySession;
        private readonly IValueServicesFacade<TValue> myValueServices;
        private readonly IOptions myOptions;
        private readonly ILogger myLogger;
        private bool? myIsGodotModule;

        protected GodotAdditionalValuesProvider(IDebuggerSession session, IValueServicesFacade<TValue> valueServices,
            IOptions options, ILogger logger)
        {
            mySession = session;
            myValueServices = valueServices;
            myOptions = options;
            myLogger = logger;
        }

        public IEnumerable<IValueEntity> GetAdditionalLocals(IStackFrame frame)
        {
            // Do nothing if "Allow property evaluations..." option is disabled.
            // The debugger works in two steps - get value entities/references, and then get value presentation.
            // Evaluation is always allowed in the first step, but depends on user options for the second. This allows
            // evaluation to calculate children, e.g. expanding the Results node of IEnumerable, but presentation might
            // require clicking "refresh". We should be returning un-evaluated value references here.
            // TODO: Make lazy
            if (!myOptions.ExtensionsEnabled || !mySession.EvaluationOptions.AllowTargetInvoke)
                yield break;
            
            // Only needed for the Cor
            // Avoid evaluating Scene when running not in the Game, for example in the unit-tests
            if (this is CorGodotAdditionalValuesProvider)
            {
                if (!myIsGodotModule.HasValue) 
                    myIsGodotModule = IsGodotProcess(frame.DebuggerSession);
                
                if (!myIsGodotModule.Value) 
                    yield break;
            }

            // Add "Current Scene" as a top level item to mimic the Hierarchy window in Godot
            var activeScene = GetCurrentScene(frame);
            if (activeScene != null)
                yield return activeScene.ToValue(myValueServices);
        }

        private static bool IsGodotProcess(IDebuggerSession session)
        {
            var processId = session.GetProcessInfo().Id;
            if (processId == null) return false;
            int pid = Convert.ToInt32(processId);
            var mainModule = Process.GetProcessById(pid).MainModule;
            return mainModule != null && mainModule.ModuleName.StartsWith("Godot");
        }

        [CanBeNull]
        private IValueReference<TValue> GetCurrentScene(IStackFrame frame)
        {
            return myLogger.CatchEvaluatorException<TValue, IValueReference<TValue>>(() =>
                {
                    var engineType = myValueServices.GetReifiedType(frame, "Godot.Engine, GodotSharp");
                    if (engineType == null)
                    {
                        myLogger.Warn("Unable to get typeof(Engine). Not a Godot project?");
                        return null;
                    }

                    var getMainLoop = engineType.MetadataType.GetMethods()
                        .FirstOrDefault(m => m.IsStatic && m.Parameters.Length == 0 && m.Name == "GetMainLoop");
                    if (getMainLoop == null)
                    {
                        myLogger.Warn("Unable to find Engine.GetMainLoop method");
                        return null;
                    }

                    // GetMainLoop can throw a exception if we call it from the wrong location
                    var mainLoop = engineType.CallStaticMethod(frame, mySession.EvaluationOptions, getMainLoop);
                    if (mainLoop == null)
                    {
                        myLogger.Warn("Unexpected response: Engine.GetMainLoop() == null");
                        return null;
                    }
                    
                    var sceneTreeType = myValueServices.GetReifiedType(frame, "Godot.SceneTree, GodotSharp");
                    if (sceneTreeType == null)
                    {
                        myLogger.Warn("Unable to get typeof(SceneTree).");
                        return null;
                    }
                    
                    var nodeType = myValueServices.GetReifiedType(frame, "Godot.Node, GodotSharp");
                    if (nodeType == null)
                    {
                        myLogger.Warn("Unable to get typeof(Node).");
                        return null;
                    }
                    
                    var mainLoopReference = new SimpleValueReference<TValue>(mainLoop, sceneTreeType.MetadataType,
                        "MainLoop", ValueOriginKind.Property,
                        ValueFlags.None | ValueFlags.IsReadOnly | ValueFlags.IsDefaultTypePresentation, frame,
                        myValueServices.RoleFactory);

                    if (!(mainLoopReference.GetPrimaryRole(mySession.EvaluationOptions) is IObjectValueRole<TValue> role))
                    {
                        myLogger.Warn("Unable to get role from 'MainLoop' reference");
                        return null;
                    }

                    var currentSceneReference = role.GetInstancePropertyReference(new[] { "CurrentScene" });
                    if (currentSceneReference == null)
                    {
                        myLogger.Warn("Unexpected response: CurrentScene == null");
                        return null;
                    }

                    return new SimpleValueReference<TValue>(currentSceneReference.GetValue(mySession.EvaluationOptions),
                        nodeType.MetadataType, "CurrentScene", ValueOriginKind.Property,
                        ValueFlags.None | ValueFlags.IsReadOnly | ValueFlags.IsDefaultTypePresentation, frame,
                        myValueServices.RoleFactory);
                }, exception => { });
        }
    }
}