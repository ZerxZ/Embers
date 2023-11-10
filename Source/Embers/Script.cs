﻿using System;
using System.Runtime.CompilerServices;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Linq;
using static Embers.Phase2;
using static Embers.SpecialTypes;
using static Embers.Api;

#nullable enable
#pragma warning disable CS1998

namespace Embers
{
    public sealed class Script
    {
        public readonly Interpreter Interpreter;
        public readonly bool AllowUnsafeApi;
        public Api Api => Interpreter.Api;
        public bool Running { get; private set; }
        public bool Stopping { get; private set; }
        public DebugLocation ApproximateLocation { get; private set; } = DebugLocation.Unknown;

        Stack<object> CurrentObject = new();
        Block CurrentBlock => CurrentObject.First<Block>();
        Scope CurrentScope => CurrentObject.First<Scope>();
        MethodScope CurrentMethodScope => CurrentObject.First<MethodScope>();
        Module CurrentModule => CurrentObject.First<Module>();
        Instance CurrentInstance => CurrentObject.First<Instance>();

        public AccessModifier CurrentAccessModifier = AccessModifier.Public;
        internal readonly ConditionalWeakTable<Exception, ExceptionInstance> ExceptionsTable = new();
        public readonly HashSet<ScriptThread> ScriptThreads = new();
        public Method? CurrentOnYield { get; private set; }

        public class Block {
            public readonly LockingDictionary<string, Instance> LocalVariables = new();
            public readonly LockingDictionary<string, Instance> Constants = new();
        }
        public class Scope : Block {
        }
        public class MethodScope : Scope {
            public readonly Method? Method;
            public MethodScope(Method method) : base() {
                Method = method;
            }
        }
        public class Module : Block {
            public readonly string Name;
            public readonly LockingDictionary<string, Method> Methods = new();
            public readonly LockingDictionary<string, Method> InstanceMethods = new();
            public readonly LockingDictionary<string, Instance> InstanceVariables = new();
            public readonly LockingDictionary<string, Instance> ClassVariables = new();
            public readonly Interpreter Interpreter;
            public readonly Module? SuperModule;
            public Module(string name, Module parent, Module? superModule = null) {
                Name = name;
                Interpreter = parent.Interpreter;
                SuperModule = superModule ?? Interpreter.Class;
            }
            public Module(string name, Interpreter interpreter, Module? superModule = null) {
                Name = name;
                Interpreter = interpreter;
                SuperModule = superModule;
            }
            public bool InheritsFrom(Module? Ancestor) {
                if (Ancestor == null) return false;
                Module? CurrentAncestor = this;
                while (CurrentAncestor != null) {
                    if (CurrentAncestor == Ancestor)
                        return true;
                    CurrentAncestor = CurrentAncestor.SuperModule;
                }
                return false;
            }
            public bool TryGetMethod(string MethodName, out Method? Method) {
                Method = TryGetMethod(Module => Module.Methods, MethodName);
                return Method != null;
            }
            public async Task<Instance> CallMethod(Script Script, string MethodName, Instances? Arguments = null, Method? OnYield = null) {
                return await CallMethod(Module => Module.Methods, null, Script, MethodName, Arguments, OnYield);
            }
            public bool TryGetInstanceMethod(string MethodName, out Method? Method) {
                Method = TryGetMethod(Module => Module.InstanceMethods, MethodName);
                return Method != null;
            }
            public async Task<Instance> CallInstanceMethod(Instance? OnInstance, Script Script, string MethodName, Instances? Arguments = null, Method? OnYield = null) {
                return await CallMethod(Module => Module.InstanceMethods, OnInstance, Script, MethodName, Arguments, OnYield);
            }
            private Method? TryGetMethod(Func<Module, LockingDictionary<string, Method>> MethodsDict, string MethodName) {
                Module? CurrentSuperModule = this;
                while (true) {
                    if (MethodsDict(CurrentSuperModule).TryFindMethod(MethodName, out Method? FindMethod)) return FindMethod;
                    CurrentSuperModule = CurrentSuperModule.SuperModule;
                    if (CurrentSuperModule == null) return null;
                }
            }
            private async Task<Instance> CallMethod(Func<Module, LockingDictionary<string, Method>> MethodsDict, Instance? OnInstance, Script Script, string MethodName, Instances? Arguments = null, Method? OnYield = null) {
                Method? Method = TryGetMethod(MethodsDict, MethodName);
                if (Method == null) {
                    throw new RuntimeException($"{Script.ApproximateLocation}: Undefined method '{MethodName}' for {Name}");
                }
                else if (Method.Name == "method_missing") {
                    // Get arguments (method_name, *args)
                    Instances MethodMissingArguments;
                    if (Arguments != null) {
                        List<Instance> GivenArguments = new(Arguments.MultiInstance);
                        GivenArguments.Insert(0, Script.Api.GetSymbol(MethodName));
                        MethodMissingArguments = new Instances(GivenArguments);
                    }
                    else {
                        MethodMissingArguments = Script.Api.GetSymbol(MethodName);
                    }
                    // Call method_missing
                    return await Method.Call(Script, OnInstance ?? new ModuleReference(this), MethodMissingArguments, OnYield);
                }
                else {
                    return await Method.Call(Script, OnInstance ?? new ModuleReference(this), Arguments, OnYield);
                }
            }
        }
        public class Class : Module {
            public Class(string name, Module parent, Module? superClass = null) : base(name, parent, superClass) {
                Setup();
            }
            public Class(string name, Interpreter interpreter) : base(name, interpreter) {
                Setup();
            }
            void Setup() {
                // Default method: new
                if (!TryGetMethod("new", out _)) {
                    Methods["new"] = new Method(async Input => {
                        Instance NewInstance = Input.Api.CreateInstanceFromClass(Input.Script, (Class)Input.Instance.Module!);
                        await NewInstance.CallInstanceMethod(Input.Script, "initialize", Input.Arguments, Input.OnYield);
                        return NewInstance;
                    }, null);
                }
                // Default method: initialize
                if (!TryGetInstanceMethod("initialize", out _)) {
                    InstanceMethods["initialize"] = new Method(async Input => {
                        return Input.Api.Nil;
                    }, 0);
                }
            }
        }
        public class Instance {
            /// <summary>Module will be null if instance is a PseudoInstance.</summary>
            public readonly Module? Module;
            public long ObjectId { get; private set; }
            public LockingDictionary<string, Instance> InstanceVariables { get; protected set; } = new();
            public LockingDictionary<string, Method> InstanceMethods { get; protected set; } = new();
            public bool IsTruthy => Object is not (null or false);
            public virtual object? Object { get { return null; } }
            public virtual bool Boolean { get { throw new RuntimeException("Instance is not a Boolean"); } }
            public virtual string String { get { throw new RuntimeException("Instance is not a String"); } }
            public virtual DynInteger Integer { get { throw new RuntimeException("Instance is not an Integer"); } }
            public virtual DynFloat Float { get { throw new RuntimeException("Instance is not a Float"); } }
            public virtual Method Proc { get { throw new RuntimeException("Instance is not a Proc"); } }
            public virtual ScriptThread? Thread { get { throw new RuntimeException("Instance is not a Thread"); } }
            public virtual IntegerRange Range { get { throw new RuntimeException("Instance is not a Range"); } }
            public virtual List<Instance> Array { get { throw new RuntimeException("Instance is not an Array"); } }
            public virtual HashDictionary Hash { get { throw new RuntimeException("Instance is not a Hash"); } }
            public virtual Exception Exception { get { throw new RuntimeException("Instance is not an Exception"); } }
            public virtual DateTimeOffset Time { get { throw new RuntimeException("Instance is not a Time"); } }
            public virtual WeakReference<Instance> WeakRef { get { throw new RuntimeException("Instance is not a WeakRef"); } }
            public virtual System.Net.Http.HttpResponseMessage HttpResponse { get { throw new RuntimeException("Instance is not a HttpResponse"); } }
            public virtual string Inspect() {
                return $"#<{Module?.Name}:0x{GetHashCode():x16}>";
            }
            public virtual string LightInspect() {
                return Inspect();
            }
            public Instance Clone(Interpreter Interpreter) {
                Instance Clone = (Instance)MemberwiseClone();
                Clone.ObjectId = Interpreter.GenerateObjectId();
                Clone.InstanceVariables = new();
                InstanceVariables.CopyTo(Clone.InstanceVariables);
                Clone.InstanceMethods = new();
                InstanceMethods.CopyTo(Clone.InstanceMethods);
                return Clone;
            }
            public static async Task<Instance> CreateFromToken(Script Script, Phase2Token Token) {
                if (Token.ProcessFormatting) {
                    string String = Token.Value!;
                    Stack<int> FormatPositions = new();
                    char? LastChara = null;
                    for (int i = 0; i < String.Length; i++) {
                        char Chara = String[i];

                        if (LastChara == '#' && Chara == '{') {
                            FormatPositions.Push(i - 1);
                        }
                        else if (Chara == '}') {
                            if (FormatPositions.TryPop(out int StartPosition)) {
                                string FirstHalf = String[..StartPosition];
                                string ToFormat = String[(StartPosition + 2)..i];
                                string SecondHalf = String[(i + 1)..];

                                string Formatted = (await Script.InternalEvaluateAsync(ToFormat)).LightInspect();
                                String = FirstHalf + Formatted + SecondHalf;
                                i = FirstHalf.Length - 1;
                            }
                        }
                        LastChara = Chara;
                    }
                    return new StringInstance(Script.Api.String, String);
                }

                return Token.Type switch {
                    Phase2TokenType.Nil => Script.Api.Nil,
                    Phase2TokenType.True => Script.Api.True,
                    Phase2TokenType.False => Script.Api.False,
                    Phase2TokenType.String => new StringInstance(Script.Api.String, Token.Value!),
                    Phase2TokenType.Symbol => Script.Api.GetSymbol(Token.Value!),
                    Phase2TokenType.Integer => new IntegerInstance(Script.Api.Integer, Token.ValueAsInteger),
                    Phase2TokenType.Float => new FloatInstance(Script.Api.Float, Token.ValueAsFloat),
                    _ => throw new InternalErrorException($"{Token.Location}: Cannot create new object from token type {Token.Type}")
                };
            }
            public Instance(Module fromModule) {
                Module = fromModule;
                if (this is not PseudoInstance) {
                    ObjectId = fromModule.Interpreter.GenerateObjectId();
                }
            }
            public Instance(Interpreter interpreter) {
                Module = null;
                if (this is not PseudoInstance) {
                    ObjectId = interpreter.GenerateObjectId();
                }
            }
            public bool TryGetInstanceMethod(string MethodName, out Method? Method) {
                if (this is not PseudoInstance) {
                    if (InstanceMethods.TryFindMethod(MethodName, out Method? FindMethod)) {
                        Method = FindMethod;
                        return true;
                    }
                }
                return Module!.TryGetInstanceMethod(MethodName, out Method);
            }
            public async Task<Instance> CallInstanceMethod(Script Script, string MethodName, Instances? Arguments = null, Method? OnYield = null) {
                if (this is not PseudoInstance || !TryGetInstanceMethod(MethodName, out Method? Method)) {
                    return await Module!.CallInstanceMethod(this, Script, MethodName, Arguments, OnYield);
                }
                else if (Method!.Name == "method_missing") {
                    // Get arguments (method_name, *args)
                    Instances MethodMissingArguments;
                    if (Arguments != null) {
                        List<Instance> GivenArguments = new(Arguments.MultiInstance);
                        GivenArguments.Insert(0, Script.Api.GetSymbol(MethodName));
                        MethodMissingArguments = new Instances(GivenArguments);
                    }
                    else {
                        MethodMissingArguments = Script.Api.GetSymbol(MethodName);
                    }
                    // Call method_missing
                    return await Method.Call(Script, this, MethodMissingArguments, OnYield);
                }
                else {
                    return await Method.Call(Script, this, Arguments, OnYield);
                }
            }
            public override int GetHashCode() {
                return Inspect().GetHashCode();
            }
        }
        public abstract class PseudoInstance : Instance {
            public PseudoInstance(Module module) : base(module) { }
            public PseudoInstance(Interpreter interpreter) : base(interpreter) { }
        }
        public class VariableReference : PseudoInstance {
            public Instance? Instance;
            public Phase2Token Token;
            public bool IsLocalReference => Module == null && Instance == null;
            public override string Inspect() {
                return $"{(Module != null ? Module.GetType().Name : (Instance != null ? Instance.Inspect() : Token.Inspect()))} var ref in {Token.Inspect()}";
            }
            public VariableReference(Module module, Phase2Token token) : base(module) {
                Token = token;
            }
            public VariableReference(Instance instance, Phase2Token token) : base(instance.Module!.Interpreter) {
                Instance = instance;
                Token = token;
            }
            public VariableReference(Phase2Token token, Interpreter interpreter) : base(interpreter) {
                Token = token;
            }
        }
        public class ScopeReference : PseudoInstance {
            public Scope Scope;
            public override string Inspect() {
                return Scope.GetType().Name;
            }
            public ScopeReference(Scope scope, Interpreter interpreter) : base(interpreter) {
                Scope = scope;
            }
        }
        public class MethodReference : PseudoInstance {
            readonly Method Method;
            public override object? Object { get { return Method; } }
            public override string Inspect() {
                return Method.ToString()!;
            }
            public MethodReference(Method method, Interpreter interpreter) : base(interpreter) {
                Method = method;
            }
        }
        public class LoopControlReference : PseudoInstance {
            public readonly LoopControlType Type;
            public bool CalledInYieldMethod;
            public LoopControlReference(LoopControlType type, Interpreter interpreter) : base(interpreter) {
                Type = type;
            }
        }
        public class ReturnReference : PseudoInstance {
            public readonly Instance ReturnValue;
            public bool CalledInYieldMethod;
            public ReturnReference(Instance returnValue, Interpreter interpreter) : base(interpreter) {
                ReturnValue = returnValue;
            }
        }
        public class StopReference : PseudoInstance {
            public readonly bool Manual;
            public bool CalledInYieldMethod;
            public StopReference(bool manual, Interpreter interpreter) : base(interpreter) {
                Manual = manual;
            }
        }
        public class ScriptThread {
            public Task? Running { get; private set; }
            public readonly Script ParentScript;
            public readonly Script ThreadScript;
            public Method? Method;
            private static readonly TimeSpan ShortTimeSpan = TimeSpan.FromMilliseconds(5);
            public ScriptThread(Script parentScript) {
                ParentScript = parentScript;
                ThreadScript = new Script(ParentScript.Interpreter, ParentScript.AllowUnsafeApi);
            }
            public async Task Run(Instances? Arguments = null, Method? OnYield = null) {
                // If already running, wait until it's finished
                if (Running != null) {
                    await Running;
                    return;
                }
                // Add thread to running threads
                lock (ParentScript.ScriptThreads)
                    ParentScript.ScriptThreads.Add(this);
                try {
                    // Create a new script
                    ThreadScript.CurrentObject = new Stack<object>(ParentScript.CurrentObject);
                    // Call the method in the script
                    Running = Method!.Call(ThreadScript, null, Arguments, OnYield);
                    while (!ThreadScript.Stopping && !ParentScript.Stopping && !Running.IsCompleted) {
                        await Running.WaitAsync(ShortTimeSpan);
                    }
                    // Stop the script
                    ThreadScript.Stop();
                }
                finally {
                    // Decrease thread counter
                    lock (ParentScript.ScriptThreads)
                        ParentScript.ScriptThreads.Remove(this);
                }
            }
            public void Stop() {
                ThreadScript.Stop();
            }
        }
        public class Method {
            public string? Name {get; private set;}
            public Func<MethodInput, Task<Instance>> Function {get; private set;}
            public readonly IntRange ArgumentCountRange;
            public readonly List<MethodArgumentExpression> ArgumentNames;
            public readonly bool Unsafe;
            public AccessModifier AccessModifier { get; private set; }
            public Method(Func<MethodInput, Task<Instance>> function, IntRange? argumentCountRange, List<MethodArgumentExpression>? argumentNames = null, bool IsUnsafe = false, AccessModifier accessModifier = AccessModifier.Public) {
                Function = function;
                ArgumentCountRange = argumentCountRange ?? new IntRange();
                ArgumentNames = argumentNames ?? new();
                Unsafe = IsUnsafe;
                AccessModifier = accessModifier;
            }
            public Method(Func<MethodInput, Task<Instance>> function, int argumentCount, List<MethodArgumentExpression>? argumentNames = null, bool IsUnsafe = false, AccessModifier accessModifier = AccessModifier.Public) {
                Function = function;
                ArgumentCountRange = new IntRange(argumentCount, argumentCount);
                ArgumentNames = argumentNames ?? new();
                Unsafe = IsUnsafe;
                AccessModifier = accessModifier;
            }
            public async Task<Instance> Call(Script Script, Instance? OnInstance, Instances? Arguments = null, Method? OnYield = null, BreakHandleType BreakHandleType = BreakHandleType.Invalid, bool CatchReturn = true, bool BypassAccessModifiers = false) {
                if (Unsafe && !Script.AllowUnsafeApi)
                    throw new RuntimeException($"{Script.ApproximateLocation}: The method '{Name}' is unavailable since 'AllowUnsafeApi' is disabled for this script.");
                if (!BypassAccessModifiers) {
                    if (AccessModifier == AccessModifier.Private) {
                        if (OnInstance != null && Script.CurrentModule != OnInstance.Module!)
                            throw new RuntimeException($"{Script.ApproximateLocation}: Private method '{Name}' called {(OnInstance != null ? $"for {OnInstance.Module!.Name}" : "")}");
                    }
                    else if (AccessModifier == AccessModifier.Protected) {
                        if (OnInstance != null && !Script.CurrentModule.InheritsFrom(OnInstance.Module!))
                            throw new RuntimeException($"{Script.ApproximateLocation}: Protected method '{Name}' called {(OnInstance != null ? $"for {OnInstance.Module!.Name}" : "")}");
                    }
                }

                Arguments ??= Instances.None;
                if (ArgumentCountRange.IsInRange(Arguments.Count)) {
                    // Create temporary scope
                    if (OnInstance != null) {
                        Script.CurrentObject.Push(OnInstance.Module!);
                        Script.CurrentObject.Push(OnInstance);
                    }
                    else if (OnInstance != null) {
                        Script.CurrentObject.Push(OnInstance.Module!);
                    }
                    MethodScope MethodScope = new(this);
                    Script.CurrentObject.Push(MethodScope);
                    
                    Instance ReturnValue;
                    try {
                        // Create method input
                        MethodInput Input = new(Script, OnInstance, Arguments, OnYield);
                        // Set argument variables
                        await SetArgumentVariables(MethodScope, Input);
                        // Call method
                        ReturnValue = await Function(Input);
                        // Handle loop control
                        if (ReturnValue is LoopControlReference LoopControlReference) {
                            // Break
                            if (LoopControlReference.Type == LoopControlType.Break) {
                                if (LoopControlReference.CalledInYieldMethod) {
                                    LoopControlReference.CalledInYieldMethod = false;
                                }
                                else if (BreakHandleType != BreakHandleType.Rethrow) {
                                    if (BreakHandleType == BreakHandleType.Destroy)
                                        ReturnValue = Script.Api.Nil;
                                    else
                                        throw new SyntaxErrorException($"{Script.ApproximateLocation}: Invalid break (break must be in a loop)");
                                }
                            }
                        }
                        // Handle return
                        else if (ReturnValue is ReturnReference ReturnReference) {
                            if (CatchReturn) {
                                if (ReturnReference.CalledInYieldMethod) {
                                    ReturnReference.CalledInYieldMethod = false;
                                }
                                else {
                                    ReturnValue = ReturnReference.ReturnValue;
                                }
                            }
                        }
                    }
                    finally {
                        // Step back a scope
                        Script.CurrentObject.Pop();
                        if (OnInstance != null) {
                            Script.CurrentObject.Pop();
                            Script.CurrentObject.Pop();
                        }
                        else if (OnInstance != null) {
                            Script.CurrentObject.Pop();
                        }
                    }
                    // Return method return value
                    return ReturnValue;
                }
                else {
                    throw new RuntimeException($"{Script.ApproximateLocation}: Wrong number of arguments for '{Name}' (given {Arguments.Count}, expected {ArgumentCountRange})");
                }
            }
            public void SetName(string? name) {
                Name = name;
                if (name == "initialize") AccessModifier = AccessModifier.Private;
            }
            public void ChangeFunction(Func<MethodInput, Task<Instance>> function) {
                Function = function;
            }
            public async Task SetArgumentVariables(Scope Scope, MethodInput Input) {
                Instances Arguments = Input.Arguments;
                // Set argument variables
                int ArgumentNameIndex = 0;
                int ArgumentIndex = 0;
                while (ArgumentNameIndex < ArgumentNames.Count) {
                    MethodArgumentExpression ArgumentName = ArgumentNames[ArgumentNameIndex];
                    string ArgumentIdentifier = ArgumentName.ArgumentName.Value!;
                    // Declare argument as variable in local scope
                    if (ArgumentIndex < Arguments.Count) {
                        // Splat argument
                        if (ArgumentName.SplatType == SplatType.Single) {
                            // Add splat arguments while there will be enough remaining arguments
                            List<Instance> SplatArguments = new();
                            while (Arguments.Count - ArgumentIndex >= ArgumentNames.Count - ArgumentNameIndex) {
                                SplatArguments.Add(Arguments[ArgumentIndex]);
                                ArgumentIndex++;
                            }
                            if (SplatArguments.Count != 0)
                                ArgumentIndex--;
                            // Add extra ungiven double splat argument if available
                            if (ArgumentNameIndex + 1 < ArgumentNames.Count && ArgumentNames[ArgumentNameIndex + 1].SplatType == SplatType.Double
                                && Arguments[^1] is not HashArgumentsInstance)
                            {
                                SplatArguments.Add(Arguments[ArgumentIndex]);
                                ArgumentIndex++;
                            }
                            // Create array from splat arguments
                            ArrayInstance SplatArgumentsArray = new(Input.Api.Array, SplatArguments);
                            // Add array to scope
                            Scope.LocalVariables[ArgumentIdentifier] = SplatArgumentsArray;
                        }
                        // Double splat argument
                        else if (ArgumentName.SplatType == SplatType.Double && Arguments[^1] is HashArgumentsInstance DoubleSplatArgumentsHash) {
                            // Add hash to scope
                            Scope.LocalVariables[ArgumentIdentifier] = DoubleSplatArgumentsHash.Value;
                        }
                        // Normal argument
                        else {
                            Scope.LocalVariables[ArgumentIdentifier] = Arguments[ArgumentIndex];
                        }
                    }
                    // Optional argument not given
                    else {
                        Instance DefaultValue = ArgumentName.DefaultValue != null ? (await Input.Script.InterpretExpressionAsync(ArgumentName.DefaultValue)) : Input.Api.Nil;
                        Scope.LocalVariables[ArgumentIdentifier] = DefaultValue;
                    }
                    ArgumentNameIndex++;
                    ArgumentIndex++;
                }
            }
        }
        public class MethodInput {
            public readonly Script Script;
            public readonly Interpreter Interpreter;
            public readonly Api Api;
            public readonly Instances Arguments;
            public readonly Method? OnYield;
            public Instance Instance => InputInstance!;
            readonly Instance? InputInstance;
            public MethodInput(Script script, Instance? instance, Instances arguments, Method? onYield = null) {
                Script = script;
                Interpreter = script.Interpreter;
                Api = Interpreter.Api;
                InputInstance = instance;
                Arguments = arguments;
                OnYield = onYield;
            }
            public DebugLocation Location => Script.ApproximateLocation;
        }
        public class Instances {
            // At least one of Instance or InstanceList will be null
            readonly Instance? Instance;
            readonly List<Instance>? InstanceList;
            public readonly int Count;

            public static readonly Instances None = new();

            public Instances(Instance? instance = null) {
                Instance = instance;
                Count = instance != null ? 1 : 0;
            }
            public Instances(List<Instance> instanceList) {
                InstanceList = instanceList;
                Count = instanceList.Count;
            }
            public Instances(params Instance[] instanceArray) {
                InstanceList = instanceArray.ToList();
                Count = InstanceList.Count;
            }
            public static implicit operator Instances(Instance Instance) {
                return new Instances(Instance);
            }
            public static implicit operator Instances(List<Instance> InstanceList) {
                return new Instances(InstanceList);
            }
            public static implicit operator Instance(Instances Instances) {
                if (Instances.Count != 1) {
                    if (Instances.Count == 0)
                        throw new RuntimeException($"Cannot implicitly cast Instances to Instance because there are none");
                    else
                        throw new RuntimeException($"Cannot implicitly cast Instances to Instance because {Instances.Count - 1} instances would be overlooked");
                }
                return Instances[0];
            }
            public Instance this[Index i] => InstanceList != null
                ? InstanceList[i]
                : (i.Value == 0 && Instance != null ? Instance : throw new ApiException("Index was outside the range of the instances"));
            public IEnumerator<Instance> GetEnumerator() {
                if (InstanceList != null) {
                    for (int i = 0; i < InstanceList.Count; i++) {
                        yield return InstanceList[i];
                    }
                }
                else if (Instance != null) {
                    yield return Instance;
                }
            }
            public Instance SingleInstance => Count == 1
                ? this[0]
                : throw new SyntaxErrorException($"Unexpected instances (expected one, got {Count})");
            public List<Instance> MultiInstance => InstanceList ?? (Instance != null
                ? new List<Instance>() { Instance }
                : new List<Instance>());
        }

        public async Task Warn(string Message) {
            await CurrentInstance.CallInstanceMethod(this, "warn", new StringInstance(Api.String, Message));
        }
        public Module CreateModule(string Name, Module? Parent = null, Module? InheritsFrom = null) {
            Parent ??= Interpreter.RootModule;
            Module NewModule = new(Name, Parent, InheritsFrom);
            Parent.Constants[Name] = new ModuleReference(NewModule);
            return NewModule;
        }
        public Class CreateClass(string Name, Module? Parent = null, Module? InheritsFrom = null) {
            Parent ??= Interpreter.RootModule;
            Class NewClass = new(Name, Parent, InheritsFrom);
            Parent.Constants[Name] = new ModuleReference(NewClass);
            return NewClass;
        }
        public Method CreateMethod(Func<MethodInput, Task<Instance>> Function, Range ArgumentCountRange, bool IsUnsafe = false) {
            Method NewMethod = new(Function, new IntRange(ArgumentCountRange), IsUnsafe: IsUnsafe, accessModifier: CurrentAccessModifier);
            return NewMethod;
        }
        public Method CreateMethod(Func<MethodInput, Task<Instance>> Function, IntRange? ArgumentCountRange, bool IsUnsafe = false) {
            Method NewMethod = new(Function, ArgumentCountRange, IsUnsafe: IsUnsafe, accessModifier: CurrentAccessModifier);
            return NewMethod;
        }
        public Method CreateMethod(Func<MethodInput, Task<Instance>> Function, int ArgumentCount, bool IsUnsafe = false) {
            Method NewMethod = new(Function, ArgumentCount, IsUnsafe: IsUnsafe, accessModifier: CurrentAccessModifier);
            return NewMethod;
        }
        async Task<T> CreateTemporaryClassScope<T>(Module Module, Func<Task<T>> Do) {
            // Create temporary class/module scope
            CurrentObject.Push(Module);
            try {
                // Do action
                return await Do();
            }
            finally {
                // Step back a class/module
                CurrentObject.Pop();
            }
        }
        async Task<T> CreateTemporaryInstanceScope<T>(Instance Instance, Func<Task<T>> Do) {
            // Create temporary instance scope
            CurrentObject.Push(Instance);
            try {
                // Do action
                return await Do();
            }
            finally {
                // Step back an instance
                CurrentObject.Pop();
            }
        }
        async Task<T> CreateTemporaryScope<T>(Scope Scope, Func<Task<T>> Do) {
            // Create temporary scope
            CurrentObject.Push(Scope);
            try {
                // Do action
                return await Do();
            }
            finally {
                // Step back a scope
                CurrentObject.Pop();
            }
        }
        async Task<T> CreateTemporaryScope<T>(Func<Task<T>> Do) {
            return await CreateTemporaryScope(new Scope(), Do);
        }
        async Task CreateTemporaryScope(Scope Scope, Func<Task> Do) {
            // Create temporary scope
            CurrentObject.Push(Scope);
            try {
                // Do action
                await Do();
            }
            finally {
                // Step back a scope
                CurrentObject.Pop();
            }
        }
        async Task CreateTemporaryScope(Func<Task> Do) {
            await CreateTemporaryScope(new Scope(), Do);
        }
        public bool TryGetLocalVariable(string Name, out Instance? LocalVariable) {
            foreach (object Object in CurrentObject) {
                if (Object is Block Block && Block.LocalVariables.TryGetValue(Name, out Instance? FindLocalVariable)) {
                    LocalVariable = FindLocalVariable;
                    return true;
                }
            }
            LocalVariable = null;
            return false;
        }
        public bool TryGetLocalConstant(string Name, out Instance? LocalConstant) {
            foreach (object Object in CurrentObject) {
                if (Object is Block Block && Block.Constants.TryGetValue(Name, out Instance? FindLocalConstant)) {
                    LocalConstant = FindLocalConstant;
                    return true;
                }
            }
            LocalConstant = null;
            return false;
        }
        public bool TryGetLocalInstanceMethod(string Name, out Method? LocalInstanceMethod) {
            foreach (object Object in CurrentObject) {
                if (Object is Instance Instance && Instance.TryGetInstanceMethod(Name, out Method? FindLocalInstanceMethod)) {
                    LocalInstanceMethod = FindLocalInstanceMethod;
                    return true;
                }
            }
            LocalInstanceMethod = null;
            return false;
        }
        public Dictionary<string, Instance> GetAllLocalVariables() {
            Dictionary<string, Instance> LocalVariables = new();
            foreach (object Object in CurrentObject) {
                if (Object is Block Block) {
                    foreach (KeyValuePair<string, Instance> LocalVariable in Block.LocalVariables) {
                        LocalVariables[LocalVariable.Key] = LocalVariable.Value;
                    }
                }
            }
            return LocalVariables;
        }
        public Dictionary<string, Instance> GetAllLocalConstants() {
            Dictionary<string, Instance> Constants = new();
            foreach (object Object in CurrentObject) {
                if (Object is Block Block) {
                    foreach (KeyValuePair<string, Instance> Constant in Block.Constants) {
                        Constants[Constant.Key] = Constant.Value;
                    }
                    if (Object is Module) break;
                }
            }
            return Constants;
        }
        internal Method? ToYieldMethod(Method? Current) {
            // This makes yield methods (do ... end) be called back in the scope they're defined in, not in the scope of the method.
            // e.g. 5.times do ... end should be called in the scope of the line, not in the instance of 5.
            // If you're modifying this function, ensure you're referencing Input.Script and not this script.
            if (Current != null) {
                Func<MethodInput, Task<Instance>> CurrentFunction = Current.Function;
                object[] OriginalSnapshot = CurrentObject.ToArray();
                Current.ChangeFunction(async Input => {
                    object[] TemporarySnapshot = Input.Script.CurrentObject.ToArray();
                    Input.Script.CurrentObject.ReplaceContentsWith(OriginalSnapshot);
                    try {
                        Instance Result = await Input.Script.CreateTemporaryScope(async () => {
                            await Current.SetArgumentVariables(Input.Script.CurrentScope, Input);
                            return await CurrentFunction(Input);
                        });
                        if (Result is LoopControlReference LoopControlReference) {
                            LoopControlReference.CalledInYieldMethod = true;
                        }
                        return Result;
                    }
                    finally {
                        Input.Script.CurrentObject.ReplaceContentsWith(TemporarySnapshot);
                    }
                });
            }
            return Current;
        }

        async Task<Instance> InterpretMethodCallExpression(MethodCallExpression MethodCallExpression) {
            Instance MethodPath = await InterpretExpressionAsync(MethodCallExpression.MethodPath, ReturnType.HypotheticalVariable);
            if (MethodPath is VariableReference MethodReference) {
                // Static method
                if (MethodReference.Module != null) {
                    // Get class/module which owns method
                    Module MethodModule = MethodReference.Module;
                    // Call class method
                    return await MethodModule.CallMethod(this, MethodReference.Token.Value!,
                        await InterpretExpressionsAsync(MethodCallExpression.Arguments), MethodCallExpression.OnYield?.ToYieldMethod(this, CurrentOnYield));
                }
                // Instance method
                else {
                    Instance MethodInstance;
                    // Local
                    if (MethodReference.IsLocalReference) {
                        MethodInstance = CurrentInstance;
                    }
                    // Path
                    else {
                        MethodInstance = MethodReference.Instance!;
                    }
                    // Call instance method
                    return await MethodInstance.CallInstanceMethod(this, MethodReference.Token.Value!,
                        await InterpretExpressionsAsync(MethodCallExpression.Arguments), MethodCallExpression.OnYield?.ToYieldMethod(this, CurrentOnYield)
                    );
                }
            }
            else {
                throw new InternalErrorException($"{MethodCallExpression.Location}: MethodPath should be VariableReference, not {MethodPath.GetType().Name}");
            }
        }
        async Task<Instance> InterpretObjectTokenExpression(ObjectTokenExpression ObjectTokenExpression, ReturnType ReturnType) {
            // Path
            if (ObjectTokenExpression is PathExpression PathExpression) {
                Instance ParentInstance = await InterpretExpressionAsync(PathExpression.ParentObject);
                // Class method
                if (ParentInstance is ModuleReference ParentModule) {
                    // Method
                    if (ReturnType != ReturnType.HypotheticalVariable) {
                        // Call class method
                        if (ReturnType == ReturnType.InterpretResult) {
                            return await ParentModule.Module!.CallMethod(this, PathExpression.Token.Value!);
                        }
                        else {
                            // Return class method
                            if (ParentModule.Module!.Methods.ContainsKey(PathExpression.Token.Value!)) {
                                return new VariableReference(ParentModule.Module, PathExpression.Token);
                            }
                            // Error
                            else {
                                throw new RuntimeException($"{PathExpression.Token.Location}: Undefined method '{PathExpression.Token.Value!}' for {ParentModule.Module.Name}");
                            }
                        }
                    }
                    // New method
                    else {
                        return new VariableReference(ParentModule.Module!, PathExpression.Token);
                    }
                }
                // Instance method
                else {
                    // Method
                    if (ReturnType != ReturnType.HypotheticalVariable) {
                        // Call instance method
                        if (ReturnType == ReturnType.InterpretResult) {
                            return await ParentInstance.CallInstanceMethod(this, PathExpression.Token.Value!);
                        }
                        else {
                            // Return instance method
                            if (ParentInstance.TryGetInstanceMethod(PathExpression.Token.Value!, out _)) {
                                return new VariableReference(ParentInstance, PathExpression.Token);
                            }
                            // Error
                            else {
                                throw new RuntimeException($"{PathExpression.Token.Location}: Undefined method '{PathExpression.Token.Value!}' for {ParentInstance.Inspect()}");
                            }
                        }
                    }
                    // New method
                    else {
                        return new VariableReference(ParentInstance, PathExpression.Token);
                    }
                }
            }
            // Constant Path
            else if (ObjectTokenExpression is ConstantPathExpression ConstantPathExpression) {
                Instance ParentInstance = await InterpretExpressionAsync(ConstantPathExpression.ParentObject);
                // Constant
                if (ReturnType != ReturnType.HypotheticalVariable) {
                    // Constant
                    if (ParentInstance.Module!.Constants.TryGetValue(ConstantPathExpression.Token.Value!, out Instance? ConstantValue)) {
                        // Return constant
                        if (ReturnType == ReturnType.InterpretResult) {
                            return ConstantValue;
                        }
                        // Return constant reference
                        else {
                            return new VariableReference(ParentInstance.Module, ConstantPathExpression.Token);
                        }
                    }
                    // Error
                    else {
                        throw new RuntimeException($"{ConstantPathExpression.Token.Location}: Uninitialized constant {ConstantPathExpression.Inspect()}");
                    }
                }
                // New constant
                else {
                    return new VariableReference(ParentInstance.Module!, ConstantPathExpression.Token);
                }
            }
            // Local
            else {
                // Literal
                if (ObjectTokenExpression.Token.IsObjectToken) {
                    return await Instance.CreateFromToken(this, ObjectTokenExpression.Token);
                }
                else {
                    if (ReturnType != ReturnType.HypotheticalVariable) {
                        switch (ObjectTokenExpression.Token.Type) {
                            // Local variable or method
                            case Phase2TokenType.LocalVariableOrMethod: {
                                // Local variable (priority)
                                if (TryGetLocalVariable(ObjectTokenExpression.Token.Value!, out Instance? Value)) {
                                    // Return local variable value
                                    if (ReturnType == ReturnType.InterpretResult) {
                                        return Value!;
                                    }
                                    // Return local variable reference
                                    else {
                                        return new VariableReference(ObjectTokenExpression.Token, Interpreter);
                                    }
                                }
                                // Method
                                else if (TryGetLocalInstanceMethod(ObjectTokenExpression.Token.Value!, out Method? Method)) {
                                    // Call local method
                                    if (ReturnType == ReturnType.InterpretResult) {
                                        return await Method!.Call(this, CurrentInstance);
                                    }
                                    // Return method reference
                                    else {
                                        return new VariableReference(ObjectTokenExpression.Token, Interpreter);
                                    }
                                }
                                // Undefined
                                else {
                                    throw new RuntimeException($"{ObjectTokenExpression.Token.Location}: Undefined local variable or method '{ObjectTokenExpression.Token.Value!}' for {CurrentObject.Peek()}");
                                }
                            }
                            // Global variable
                            case Phase2TokenType.GlobalVariable: {
                                if (Interpreter.GlobalVariables.TryGetValue(ObjectTokenExpression.Token.Value!, out Instance? Value)) {
                                    // Return global variable value
                                    if (ReturnType == ReturnType.InterpretResult) {
                                        return Value;
                                    }
                                    // Return global variable reference
                                    else {
                                        return new VariableReference(ObjectTokenExpression.Token, Interpreter);
                                    }
                                }
                                else {
                                    return Api.Nil;
                                }
                            }
                            // Constant
                            case Phase2TokenType.ConstantOrMethod: {
                                // Constant (priority)
                                if (TryGetLocalConstant(ObjectTokenExpression.Token.Value!, out Instance? ConstantValue)) {
                                    // Return constant value
                                    if (ReturnType == ReturnType.InterpretResult) {
                                        return ConstantValue!;
                                    }
                                    // Return constant reference
                                    else {
                                        return new VariableReference(ObjectTokenExpression.Token, Interpreter);
                                    }
                                }
                                // Method
                                else if (TryGetLocalInstanceMethod(ObjectTokenExpression.Token.Value!, out Method? Method)) {
                                    // Call local method
                                    if (ReturnType == ReturnType.InterpretResult) {
                                        return await Method!.Call(this, CurrentInstance);
                                    }
                                    // Return method reference
                                    else {
                                        return new VariableReference(ObjectTokenExpression.Token, Interpreter);
                                    }
                                }
                                // Uninitialized
                                else {
                                    throw new RuntimeException($"{ObjectTokenExpression.Token.Location}: Uninitialized constant '{ObjectTokenExpression.Token.Value!}' for {CurrentModule.Name}");
                                }
                            }
                            // Instance variable
                            case Phase2TokenType.InstanceVariable: {
                                if (CurrentInstance.InstanceVariables.TryGetValue(ObjectTokenExpression.Token.Value!, out Instance? Value)) {
                                    // Return instance variable value
                                    if (ReturnType == ReturnType.InterpretResult) {
                                        return Value;
                                    }
                                    // Return instance variable reference
                                    else {
                                        return new VariableReference(ObjectTokenExpression.Token, Interpreter);
                                    }
                                }
                                else {
                                    return Api.Nil;
                                }
                            }
                            // Class variable
                            case Phase2TokenType.ClassVariable: {
                                if (CurrentModule.ClassVariables.TryGetValue(ObjectTokenExpression.Token.Value!, out Instance? Value)) {
                                    // Return class variable value
                                    if (ReturnType == ReturnType.InterpretResult) {
                                        return Value;
                                    }
                                    // Return class variable reference
                                    else {
                                        return new VariableReference(ObjectTokenExpression.Token, Interpreter);
                                    }
                                }
                                else {
                                    throw new RuntimeException($"{ObjectTokenExpression.Token.Location}: Uninitialized class variable '{ObjectTokenExpression.Token.Value!}' for {CurrentModule}");
                                }
                            }
                            // Error
                            default:
                                throw new InternalErrorException($"{ObjectTokenExpression.Token.Location}: Unknown variable type {ObjectTokenExpression.Token.Type}");
                        }
                    }
                    // Variable
                    else {
                        return new VariableReference(ObjectTokenExpression.Token, Interpreter);
                    }
                }
            }
        }
        async Task<Instance> InterpretIfExpression(IfExpression IfExpression) {
            if (IfExpression.Condition == null || (await InterpretExpressionAsync(IfExpression.Condition)).IsTruthy != IfExpression.Inverse) {
                return await InternalInterpretAsync(IfExpression.Statements, CurrentOnYield);
            }
            return Api.Nil;
        }
        async Task<Instance> InterpretRescueExpression(RescueExpression RescueExpression) {
            try {
                await InterpretExpressionAsync(RescueExpression.Statement);
            }
            catch {
                await InterpretExpressionAsync(RescueExpression.RescueStatement);
            }
            return Api.Nil;
        }
        async Task<Instance> InterpretTernaryExpression(TernaryExpression TernaryExpression) {
            bool ConditionIsTruthy = (await InterpretExpressionAsync(TernaryExpression.Condition)).IsTruthy;
            if (ConditionIsTruthy) {
                return await InterpretExpressionAsync(TernaryExpression.ExpressionIfTrue);
            }
            else {
                return await InterpretExpressionAsync(TernaryExpression.ExpressionIfFalse);
            }
        }
        async Task<Instance> InterpretCaseExpression(CaseExpression CaseExpression) {
            Instance Subject = await InterpretExpressionAsync(CaseExpression.Subject);
            foreach (WhenExpression Branch in CaseExpression.Branches) {
                // Check if when statements apply
                bool WhenApplies = false;
                // When
                if (Branch.Conditions.Count != 0) {
                    foreach (Expression Condition in Branch.Conditions) {
                        Instance ConditionObject = await InterpretExpressionAsync(Condition);
                        if ((await ConditionObject.CallInstanceMethod(this, "===", Subject)).IsTruthy) {
                            WhenApplies = true;
                        }
                    }
                }
                // Else
                else {
                    WhenApplies = true;
                }
                // Run when statements
                if (WhenApplies) {
                    return await InternalInterpretAsync(Branch.Statements, CurrentOnYield);
                }
            }
            return Api.Nil;
        }
        async Task<ArrayInstance> InterpretArrayExpression(ArrayExpression ArrayExpression) {
            List<Instance> Items = new();
            foreach (Expression Item in ArrayExpression.Expressions) {
                Items.Add(await InterpretExpressionAsync(Item));
            }
            return new ArrayInstance(Api.Array, Items);
        }
        async Task<HashInstance> InterpretHashExpression(HashExpression HashExpression) {
            HashDictionary Items = new();
            foreach (KeyValuePair<Expression, Expression> Item in HashExpression.Expressions) {
                await Items.Store(this, await InterpretExpressionAsync(Item.Key), await InterpretExpressionAsync(Item.Value));
            }
            return new HashInstance(Api.Hash, Items, Api.Nil);
        }
        async Task<Instance> InterpretWhileExpression(WhileExpression WhileExpression) {
            while ((await InterpretExpressionAsync(WhileExpression.Condition!)).IsTruthy != WhileExpression.Inverse) {
                Instance Result = await InternalInterpretAsync(WhileExpression.Statements, CurrentOnYield);
                if (Result is LoopControlReference LoopControlReference) {
                    if (LoopControlReference.Type is LoopControlType.Break) {
                        break;
                    }
                    else if (LoopControlReference.Type is LoopControlType.Retry) {
                        throw new SyntaxErrorException($"{ApproximateLocation}: Retry not valid in while loop");
                    }
                    else if (LoopControlReference.Type is LoopControlType.Redo or LoopControlType.Next) {
                        continue;
                    }
                }
            }
            return Api.Nil;
        }
        async Task<Instance> InterpretWhileStatement(WhileStatement WhileStatement) {
            // Run statements
            await CreateTemporaryScope(async () =>
                await InterpretExpressionAsync(WhileStatement.WhileExpression)
            );
            return Api.Nil;
        }
        async Task<Instance> InterpretForStatement(ForStatement ForStatement) {
            Instance InResult = await InterpretExpressionAsync(ForStatement.InExpression);
            await InResult.CallInstanceMethod(this, "each", OnYield: ForStatement.BlockStatementsMethod);
            return Api.Nil;
        }
        async Task<Instance> InterpretLogicalExpression(LogicalExpression LogicalExpression) {
            Instance Left = await InterpretExpressionAsync(LogicalExpression.Left);
            switch (LogicalExpression.LogicType) {
                case LogicalExpression.LogicalExpressionType.And:
                    if (!Left.IsTruthy)
                        return Left;
                    break;
            }
            Instance Right = await InterpretExpressionAsync(LogicalExpression.Right);
            switch (LogicalExpression.LogicType) {
                case LogicalExpression.LogicalExpressionType.And:
                    return Right;
                case LogicalExpression.LogicalExpressionType.Or:
                    if (Left.IsTruthy)
                        return Left;
                    else
                        return Right;
                case LogicalExpression.LogicalExpressionType.Xor:
                    if (Left.IsTruthy && !Right.IsTruthy)
                        return Left;
                    else if (!Left.IsTruthy && Right.IsTruthy)
                        return Right;
                    else
                        return Api.False;
                default:
                    throw new InternalErrorException($"{LogicalExpression.Location}: Unhandled logical expression type: '{LogicalExpression.LogicType}'");
            }
        }
        async Task<Instance> InterpretNotExpression(NotExpression NotExpression) {
            Instance Right = await InterpretExpressionAsync(NotExpression.Right);
            return Right.IsTruthy ? Api.False : Api.True;
        }
        async Task<Instance> InterpretDefineMethodStatement(DefineMethodStatement DefineMethodStatement) {
            Instance MethodNameObject = await InterpretExpressionAsync(DefineMethodStatement.MethodName, ReturnType.HypotheticalVariable);
            if (MethodNameObject is VariableReference MethodNameRef) {
                string MethodName = MethodNameRef.Token.Value!;
                // Define static method
                if (MethodNameRef.Module != null) {
                    Module MethodModule = MethodNameRef.Module;
                    // Prevent redefining unsafe API methods
                    if (!AllowUnsafeApi && MethodModule.Methods.TryGetValue(MethodName, out Method? ExistingMethod) && ExistingMethod.Unsafe) {
                        throw new RuntimeException($"{DefineMethodStatement.Location}: The static method '{MethodName}' cannot be redefined since 'AllowUnsafeApi' is disabled for this script.");
                    }
                    // Create or overwrite static method
                    MethodModule.Methods[MethodName] = DefineMethodStatement.MethodExpression.ToMethod(CurrentAccessModifier);
                }
                // Define instance method
                else {
                    Instance MethodInstance = MethodNameRef.Instance ?? CurrentInstance;
                    // Prevent redefining unsafe API methods
                    if (!AllowUnsafeApi && MethodInstance.TryGetInstanceMethod(MethodName, out Method? ExistingMethod) && ExistingMethod!.Unsafe) {
                        throw new RuntimeException($"{DefineMethodStatement.Location}: The instance method '{MethodName}' cannot be redefined since 'AllowUnsafeApi' is disabled for this script.");
                    }
                    // Create or overwrite instance method
                    Method NewInstanceMethod = DefineMethodStatement.MethodExpression.ToMethod(CurrentAccessModifier);
                    if (MethodNameRef.Instance != null) {
                        // Define method for a specific instance
                        MethodInstance.InstanceMethods[MethodName] = NewInstanceMethod;
                    }
                    else {
                        // Define method for all instances of a class
                        MethodInstance.Module!.InstanceMethods[MethodName] = NewInstanceMethod;
                    }
                }
            }
            else {
                throw new InternalErrorException($"{DefineMethodStatement.Location}: Invalid method name: {MethodNameObject}");
            }
            return Api.Nil;
        }
        async Task<Instance> InterpretDefineClassStatement(DefineClassStatement DefineClassStatement) {
            Instance ClassNameObject = await InterpretExpressionAsync(DefineClassStatement.ClassName, ReturnType.HypotheticalVariable);
            if (ClassNameObject is VariableReference ClassNameRef) {
                string ClassName = ClassNameRef.Token.Value!;
                Module? InheritsFrom = DefineClassStatement.InheritsFrom != null ? (await InterpretExpressionAsync(DefineClassStatement.InheritsFrom)).Module : null;

                // Create or patch class
                Module NewModule;
                // Patch class
                if ((ClassNameRef.Module != null && ClassNameRef.Module.Constants.TryGetValue(ClassName, out Instance? ConstantValue) || ClassNameRef.Module == null && CurrentModule.Constants.TryGetValue(ClassName, out ConstantValue))
                    && ConstantValue is ModuleReference ModuleReference)
                {
                    if (InheritsFrom != null) {
                        throw new SyntaxErrorException($"{DefineClassStatement.Location}: Patch for already defined class/module cannot inherit");
                    }
                    NewModule = ModuleReference.Module!;
                }
                // Create class
                else {
                    if (DefineClassStatement.IsModule) {
                        if (ClassNameRef.Module != null) {
                            NewModule = CreateModule(ClassName, ClassNameRef.Module, InheritsFrom);
                        }
                        else {
                            NewModule = CreateModule(ClassName, (ClassNameRef.Instance ?? CurrentInstance).Module, InheritsFrom);
                        }
                    }
                    else {
                        if (ClassNameRef.Module != null) {
                            NewModule = CreateClass(ClassName, ClassNameRef.Module, InheritsFrom);
                        }
                        else {
                            NewModule = CreateClass(ClassName, (ClassNameRef.Instance ?? CurrentInstance).Module, InheritsFrom);
                        }
                    }
                }

                // Interpret class statements
                AccessModifier PreviousAccessModifier = CurrentAccessModifier;
                CurrentAccessModifier = AccessModifier.Public;
                await CreateTemporaryClassScope(NewModule, async () =>
                    await CreateTemporaryInstanceScope(new ModuleReference(NewModule), async () =>
                        await InternalInterpretAsync(DefineClassStatement.BlockStatements, CurrentOnYield)
                    )
                );
                CurrentAccessModifier = PreviousAccessModifier;
            }
            else {
                throw new InternalErrorException($"{DefineClassStatement.Location}: Invalid class/module name: {ClassNameObject}");
            }
            return Api.Nil;
        }
        async Task<Instance> InterpretYieldExpression(YieldExpression YieldExpression) {
            if (CurrentOnYield != null) {
                List<Instance> YieldArgs = YieldExpression.YieldValues != null
                    ? await InterpretExpressionsAsync(YieldExpression.YieldValues)
                    : new();
                return await CurrentOnYield.Call(this, null, YieldArgs, BreakHandleType: BreakHandleType.Destroy, CatchReturn: false);
            }
            else {
                throw new RuntimeException($"{YieldExpression.Location}: No block given to yield to");
            }
        }
        async Task<Instance> InterpretSuperExpression(SuperExpression SuperExpression) {
            Module CurrentModule = this.CurrentModule;
            string? SuperMethodName = CurrentMethodScope.Method?.Name;
            if (CurrentModule != Interpreter.RootModule && CurrentModule.SuperModule is Module SuperModule) {
                if (SuperMethodName != null && SuperModule.TryGetInstanceMethod(SuperMethodName, out Method? SuperMethod)) {
                    Instances? Arguments = null;
                    if (SuperExpression.Arguments != null) {
                        Arguments = await InterpretExpressionsAsync(SuperExpression.Arguments);
                    }
                    return await SuperMethod!.Call(this, null, Arguments, BypassAccessModifiers: true);
                }
            }
            throw new RuntimeException($"{SuperExpression.Location}: No super method '{SuperMethodName}' to call");
        }
        async Task<Instance> InterpretAliasStatement(AliasStatement AliasStatement) {
            Instance MethodAlias = await InterpretExpressionAsync(AliasStatement.AliasAs, ReturnType.HypotheticalVariable);
            if (MethodAlias is VariableReference MethodAliasRef) {
                Instance MethodOrigin = await InterpretExpressionAsync(AliasStatement.MethodToAlias, ReturnType.FoundVariable);
                if (MethodOrigin is VariableReference MethodOriginRef) {
                    // Get target methods dictionary
                    LockingDictionary<string, Method> TargetMethods = MethodAliasRef.Instance != null ? MethodAliasRef.Instance.InstanceMethods
                        : (MethodAliasRef.Module != null ? MethodAliasRef.Module.Methods : CurrentInstance.InstanceMethods);
                    // Get origin method
                    Method OriginMethod;
                    if (MethodOriginRef.Instance != null) {
                        MethodOriginRef.Instance.TryGetInstanceMethod(MethodOriginRef.Token.Value!, out OriginMethod!);
                    }
                    else if (MethodOriginRef.Module != null) {
                        MethodOriginRef.Module.TryGetMethod(MethodOriginRef.Token.Value!, out OriginMethod!);
                    }
                    else {
                        CurrentInstance.TryGetInstanceMethod(MethodOriginRef.Token.Value!, out OriginMethod!);
                    }
                    // Create alias for method
                    TargetMethods[AliasStatement.AliasAs.Token.Value!] = OriginMethod;
                }
                else {
                    throw new SyntaxErrorException($"{AliasStatement.Location}: Expected method to alias, got '{MethodOrigin.Inspect()}'");
                }
            }
            else {
                throw new SyntaxErrorException($"{AliasStatement.Location}: Expected method alias, got '{MethodAlias.Inspect()}'");
            }
            return Api.Nil;
        }
        async Task<Instance> InterpretRangeExpression(RangeExpression RangeExpression) {
            Instance? RawMin = null;
            if (RangeExpression.Min != null) RawMin = await InterpretExpressionAsync(RangeExpression.Min);
            Instance? RawMax = null;
            if (RangeExpression.Max != null) RawMax = await InterpretExpressionAsync(RangeExpression.Max);

            if (RawMin is IntegerInstance Min && RawMax is IntegerInstance Max) {
                return new RangeInstance(Api.Range, Min, Max, RangeExpression.IncludesMax);
            }
            else if (RawMin == null && RawMax is IntegerInstance MaxOnly) {
                return new RangeInstance(Api.Range, null, MaxOnly, RangeExpression.IncludesMax);
            }
            else if (RawMax == null && RawMin is IntegerInstance MinOnly) {
                return new RangeInstance(Api.Range, MinOnly, null, RangeExpression.IncludesMax);
            }
            else {
                throw new RuntimeException($"{RangeExpression.Location}: Range bounds must be integers (got '{RawMin?.LightInspect()}' and '{RawMax?.LightInspect()}')");
            }
        }
        async Task<Instance> InterpretIfBranchesStatement(IfBranchesStatement IfStatement) {
            for (int i = 0; i < IfStatement.Branches.Count; i++) {
                IfExpression Branch = IfStatement.Branches[i];
                // If / elsif
                if (Branch.Condition != null) {
                    Instance ConditionResult = await InterpretExpressionAsync(Branch.Condition);
                    if (ConditionResult.IsTruthy != Branch.Inverse) {
                        // Run statements
                        return await CreateTemporaryScope(async () =>
                            await InternalInterpretAsync(Branch.Statements, CurrentOnYield)
                        );
                    }
                }
                // Else
                else {
                    // Run statements
                    return await CreateTemporaryScope(async () =>
                        await InternalInterpretAsync(Branch.Statements, CurrentOnYield)
                    );
                }
            }
            return Api.Nil;
        }
        async Task<Instance> InterpretBeginBranchesStatement(BeginBranchesStatement BeginBranchesStatement) {
            // Begin
            BeginStatement BeginBranch = (BeginStatement)BeginBranchesStatement.Branches[0];
            Exception? ExceptionToRescue = null;
            try {
                await CreateTemporaryScope(async () =>
                    // Run statements
                    await InternalInterpretAsync(BeginBranch.Statements, CurrentOnYield)
                );
            }
            catch (Exception Ex) {
                ExceptionToRescue = Ex;
            }

            // Rescue
            bool Rescued = false;
            if (ExceptionToRescue != null) {
                // Find a rescue statement that can rescue the given error
                for (int i = 1; i < BeginBranchesStatement.Branches.Count; i++) {
                    BeginComponentStatement Branch = BeginBranchesStatement.Branches[i];
                    if (Branch is RescueStatement RescueStatement) {
                        // Get or create the exception to rescue
                        ExceptionsTable.TryGetValue(ExceptionToRescue, out ExceptionInstance? ExceptionInstance);
                        ExceptionInstance ??= new ExceptionInstance(Api.RuntimeError, ExceptionToRescue);
                        // Get the rescuing exception type
                        Module RescuingExceptionModule = RescueStatement.Exception != null
                            ? (await InterpretExpressionAsync(RescueStatement.Exception)).Module!
                            : Api.StandardError;

                        // Check whether rescue applies to this exception
                        bool CanRescue = false;
                        if (ExceptionInstance.Module!.InheritsFrom(RescuingExceptionModule)) {
                            CanRescue = true;
                        }

                        // Run the statements in the rescue block
                        if (CanRescue) {
                            Rescued = true;
                            await CreateTemporaryScope(async () => {
                                // Set exception variable to exception instance
                                if (RescueStatement.ExceptionVariable != null) {
                                    CurrentScope.LocalVariables[RescueStatement.ExceptionVariable.Value!] = ExceptionInstance;
                                }
                                await InternalInterpretAsync(RescueStatement.Statements, CurrentOnYield);
                            });
                            break;
                        }
                    }
                }
                // Rethrow exception if not rescued
                if (!Rescued) throw ExceptionToRescue;
            }

            // Ensure & Else
            for (int i = 1; i < BeginBranchesStatement.Branches.Count; i++) {
                BeginComponentStatement Branch = BeginBranchesStatement.Branches[i];
                if (Branch is EnsureStatement || (Branch is RescueElseStatement && !Rescued)) {
                    // Run statements
                    await CreateTemporaryScope(async () =>
                        await InternalInterpretAsync(Branch.Statements, CurrentOnYield)
                    );
                }
            }

            return Api.Nil;
        }
        async Task AssignToVariable(VariableReference Variable, Instance Value) {
            switch (Variable.Token.Type) {
                case Phase2TokenType.LocalVariableOrMethod:
                    // call instance.variable=
                    if (Variable.Instance != null) {
                        await Variable.Instance.CallInstanceMethod(this, Variable.Token.Value! + "=", Value);
                    }
                    // set variable =
                    else {
                        // Find appropriate local variable block
                        Block SetBlock = CurrentBlock;
                        foreach (object Object in CurrentObject) {
                            if (Object is Block Block) {
                                if (Block.LocalVariables.ContainsKey(Variable.Token.Value!)) {
                                    SetBlock = Block;
                                    break;
                                }
                            }
                            else break;
                        }
                        // Set local variable
                        SetBlock.LocalVariables[Variable.Token.Value!] = Value;
                    }
                    break;
                case Phase2TokenType.GlobalVariable:
                    Interpreter.GlobalVariables[Variable.Token.Value!] = Value;
                    break;
                case Phase2TokenType.ConstantOrMethod:
                    if (CurrentBlock.Constants.ContainsKey(Variable.Token.Value!))
                        await Warn($"{Variable.Token.Location}: Already initialized constant '{Variable.Token.Value!}'");
                    CurrentBlock.Constants[Variable.Token.Value!] = Value;
                    break;
                case Phase2TokenType.InstanceVariable:
                    CurrentInstance.InstanceVariables[Variable.Token.Value!] = Value;
                    break;
                case Phase2TokenType.ClassVariable:
                    CurrentModule.ClassVariables[Variable.Token.Value!] = Value;
                    break;
                default:
                    throw new InternalErrorException($"{Variable.Token.Location}: Assignment variable token is not a variable type (got {Variable.Token.Type})");
            }
        }
        async Task<Instance> InterpretAssignmentExpression(AssignmentExpression AssignmentExpression, ReturnType ReturnType) {
            Instance Right = await InterpretExpressionAsync(AssignmentExpression.Right);
            Instance Left = await InterpretExpressionAsync(AssignmentExpression.Left, ReturnType.HypotheticalVariable);

            if (Left is VariableReference LeftVariable) {
                if (Right is Instance RightInstance) {
                    // LeftVariable = RightInstance
                    await AssignToVariable(LeftVariable, RightInstance);
                    // Return left variable reference or value
                    if (ReturnType == ReturnType.InterpretResult) {
                        return RightInstance;
                    }
                    else {
                        return Left;
                    }
                }
                else {
                    throw new InternalErrorException($"{LeftVariable.Token.Location}: Assignment value should be an instance, but got {Right.GetType().Name}");
                }
            }
            else {
                throw new RuntimeException($"{AssignmentExpression.Left.Location}: {Left.GetType()} cannot be the target of an assignment");
            }
        }
        async Task<Instance> InterpretMultipleAssignmentExpression(MultipleAssignmentExpression MultipleAssignmentExpression, ReturnType ReturnType) {
            // Check if assigning variables from array (e.g. a, b = [c, d])
            Instance FirstRight = await InterpretExpressionAsync(MultipleAssignmentExpression.Right[0]);
            ArrayInstance? AssigningFromArray = null;
            if (MultipleAssignmentExpression.Right.Count == 1 && FirstRight is ArrayInstance AssignmentValueArray) {
                AssigningFromArray = AssignmentValueArray;
            }
            // Assign each variable to each value
            List<Instance> AssignedValues = new();
            for (int i = 0; i < MultipleAssignmentExpression.Left.Count; i++) {
                Instance Right = AssigningFromArray == null
                    ? i != 0 ? await InterpretExpressionAsync(MultipleAssignmentExpression.Right[i]) : FirstRight
                    : await AssigningFromArray.CallInstanceMethod(this, "[]", new IntegerInstance(Api.Integer, i));
                Instance Left = await InterpretExpressionAsync(MultipleAssignmentExpression.Left[i], ReturnType.HypotheticalVariable);

                if (Left is VariableReference LeftVariable) {
                    if (Right is Instance RightInstance) {
                        // LeftVariable = RightInstance
                        await AssignToVariable(LeftVariable, RightInstance);
                        // Return left variable reference or value
                        if (ReturnType == ReturnType.InterpretResult) {
                            AssignedValues.Add(RightInstance);
                        }
                        else {
                            throw new InternalErrorException($"{MultipleAssignmentExpression.Location}: Cannot get variable reference from multiple assignment");
                        }
                    }
                    else {
                        throw new InternalErrorException($"{LeftVariable.Token.Location}: Assignment value should be an instance, but got {Right.GetType().Name}");
                    }
                }
                else {
                    throw new RuntimeException($"{MultipleAssignmentExpression.Left[i].Location}: {Left.GetType()} cannot be the target of an assignment");
                }
            }
            return new ArrayInstance(Api.Array, AssignedValues);
        }
        async Task<Instance> InterpretUndefineMethodStatement(UndefineMethodStatement UndefineMethodStatement) {
            string MethodName = UndefineMethodStatement.MethodName.Token.Value!;
            if (MethodName == "initialize") {
                await Warn($"{UndefineMethodStatement.MethodName.Token.Location}: Undefining 'initialize' may cause problems");
            }
            if (!CurrentModule.InstanceMethods.Remove(MethodName)) {
                throw new RuntimeException($"{UndefineMethodStatement.MethodName.Token.Location}: Undefined method '{MethodName}' for {CurrentModule.Name}");
            }
            return Api.Nil;
        }
        async Task<Instance> InterpretDefinedExpression(DefinedExpression DefinedExpression) {
            if (DefinedExpression.Expression is MethodCallExpression DefinedMethod) {
                try {
                    await InterpretExpressionAsync(DefinedMethod.MethodPath, ReturnType.FoundVariable);
                }
                catch (RuntimeException) {
                    return Api.Nil;
                }
                return new StringInstance(Api.String, "method");
            }
            if (DefinedExpression.Expression is PathExpression DefinedPath) {
                try {
                    await InterpretExpressionAsync(DefinedPath, ReturnType.FoundVariable);
                }
                catch (RuntimeException) {
                    return Api.Nil;
                }
                return new StringInstance(Api.String, "method");
            }
            else if (DefinedExpression.Expression is ObjectTokenExpression ObjectToken) {
                if (ObjectToken.Token.Type == Phase2TokenType.LocalVariableOrMethod) {
                    if (TryGetLocalVariable(ObjectToken.Token.Value!, out _)) {
                        return new StringInstance(Api.String, "local-variable");
                    }
                    else if (TryGetLocalInstanceMethod(ObjectToken.Token.Value!, out _)) {
                        return new StringInstance(Api.String, "method");
                    }
                    else {
                        return Api.Nil;
                    }
                }
                else if (ObjectToken.Token.Type == Phase2TokenType.GlobalVariable) {
                    if (Interpreter.GlobalVariables.ContainsKey(ObjectToken.Token.Value!)) {
                        return new StringInstance(Api.String, "global-variable");
                    }
                    else {
                        return Api.Nil;
                    }
                }
                else if (ObjectToken.Token.Type == Phase2TokenType.ConstantOrMethod) {
                    if (TryGetLocalConstant(ObjectToken.Token.Value!, out _)) {
                        return new StringInstance(Api.String, "constant");
                    }
                    else if (TryGetLocalInstanceMethod(ObjectToken.Token.Value!, out _)) {
                        return new StringInstance(Api.String, "method");
                    }
                    else {
                        return Api.Nil;
                    }
                }
                else if (ObjectToken.Token.Type == Phase2TokenType.InstanceVariable) {
                    if (CurrentInstance.InstanceVariables.ContainsKey(ObjectToken.Token.Value!)) {
                        return new StringInstance(Api.String, "instance-variable");
                    }
                    else {
                        return Api.Nil;
                    }
                }
                else if (ObjectToken.Token.Type == Phase2TokenType.ClassVariable) {
                    if (CurrentModule.ClassVariables.ContainsKey(ObjectToken.Token.Value!)) {
                        return new StringInstance(Api.String, "class-variable");
                    }
                    else {
                        return Api.Nil;
                    }
                }
                else {
                    return new StringInstance(Api.String, "expression");
                }
            }
            else if (DefinedExpression.Expression is SelfExpression) {
                return new StringInstance(Api.String, "self");
            }
            else if (DefinedExpression.Expression is SuperExpression) {
                return new StringInstance(Api.String, "super");
            }
            else {
                return new StringInstance(Api.String, "expression");
            }
        }
        async Task<Instance> InterpretHashArgumentsExpression(HashArgumentsExpression HashArgumentsExpression) {
            return new HashArgumentsInstance(
                await InterpretHashExpression(HashArgumentsExpression.HashExpression),
                Interpreter
            );
        }
        async Task<Instance> InterpretEnvironmentInfoExpression(EnvironmentInfoExpression EnvironmentInfoExpression) {
            return EnvironmentInfoExpression.Type switch {
                EnvironmentInfoType.__LINE__ => new IntegerInstance(Api.Integer, EnvironmentInfoExpression.Location.Line),
                EnvironmentInfoType.__FILE__ => new StringInstance(Api.String, System.IO.Path.GetFileName(new System.Diagnostics.StackTrace(true).GetFrame(0)?.GetFileName() ?? "")),
                _ => throw new InternalErrorException($"{ApproximateLocation}: Environment info type not handled: '{EnvironmentInfoExpression.Type}'"),
            };
        }

        public enum AccessModifier {
            Public,
            Private,
            Protected,
        }
        public enum BreakHandleType {
            Invalid,
            Rethrow,
            Destroy
        }
        public enum ReturnType {
            InterpretResult,
            FoundVariable,
            HypotheticalVariable
        }
        async Task<Instance> InterpretExpressionAsync(Expression Expression, ReturnType ReturnType = ReturnType.InterpretResult) {
            // Set approximate location
            ApproximateLocation = Expression.Location;

            // Stop script
            if (Stopping)
                return new StopReference(false, Interpreter);

            // Interpret expression
            return Expression switch {
                MethodCallExpression MethodCallExpression => await InterpretMethodCallExpression(MethodCallExpression),
                ObjectTokenExpression ObjectTokenExpression => await InterpretObjectTokenExpression(ObjectTokenExpression, ReturnType),
                IfExpression IfExpression => await InterpretIfExpression(IfExpression),
                WhileExpression WhileExpression => await InterpretWhileExpression(WhileExpression),
                RescueExpression RescueExpression => await InterpretRescueExpression(RescueExpression),
                TernaryExpression TernaryExpression => await InterpretTernaryExpression(TernaryExpression),
                CaseExpression CaseExpression => await InterpretCaseExpression(CaseExpression),
                ArrayExpression ArrayExpression => await InterpretArrayExpression(ArrayExpression),
                HashExpression HashExpression => await InterpretHashExpression(HashExpression),
                WhileStatement WhileStatement => await InterpretWhileStatement(WhileStatement),
                ForStatement ForStatement => await InterpretForStatement(ForStatement),
                SelfExpression => CurrentInstance,
                LogicalExpression LogicalExpression => await InterpretLogicalExpression(LogicalExpression),
                NotExpression NotExpression => await InterpretNotExpression(NotExpression),
                DefineMethodStatement DefineMethodStatement => await InterpretDefineMethodStatement(DefineMethodStatement),
                DefineClassStatement DefineClassStatement => await InterpretDefineClassStatement(DefineClassStatement),
                ReturnStatement ReturnStatement => new ReturnReference(ReturnStatement.ReturnValue != null
                                                        ? await InterpretExpressionAsync(ReturnStatement.ReturnValue) : Api.Nil,
                                                        Interpreter),
                LoopControlStatement LoopControlStatement => LoopControlStatement.Type switch {
                    LoopControlType.Break => new LoopControlReference(LoopControlType.Break, Interpreter),
                    LoopControlType.Retry => new LoopControlReference(LoopControlType.Retry, Interpreter),
                    LoopControlType.Redo => new LoopControlReference(LoopControlType.Redo, Interpreter),
                    LoopControlType.Next => new LoopControlReference(LoopControlType.Next, Interpreter),
                    _ => throw new InternalErrorException($"{Expression.Location}: Loop control type not handled: '{LoopControlStatement.Type}'")},
                YieldExpression YieldExpression => await InterpretYieldExpression(YieldExpression),
                SuperExpression SuperExpression => await InterpretSuperExpression(SuperExpression),
                AliasStatement AliasStatement => await InterpretAliasStatement(AliasStatement),
                RangeExpression RangeExpression => await InterpretRangeExpression(RangeExpression),
                IfBranchesStatement IfBranchesStatement => await InterpretIfBranchesStatement(IfBranchesStatement),
                BeginBranchesStatement BeginBranchesStatement => await InterpretBeginBranchesStatement(BeginBranchesStatement),
                AssignmentExpression AssignmentExpression => await InterpretAssignmentExpression(AssignmentExpression, ReturnType),
                MultipleAssignmentExpression MultipleAssignmentExpression => await InterpretMultipleAssignmentExpression(MultipleAssignmentExpression, ReturnType),
                UndefineMethodStatement UndefineMethodStatement => await InterpretUndefineMethodStatement(UndefineMethodStatement),
                DefinedExpression DefinedExpression => await InterpretDefinedExpression(DefinedExpression),
                HashArgumentsExpression HashArgumentsExpression => await InterpretHashArgumentsExpression(HashArgumentsExpression),
                EnvironmentInfoExpression EnvironmentInfoExpression => await InterpretEnvironmentInfoExpression(EnvironmentInfoExpression),
                _ => throw new InternalErrorException($"{Expression.Location}: Not sure how to interpret expression {Expression.GetType().Name} ({Expression.Inspect()})"),
            };
        }
        async Task<List<Instance>> InterpretExpressionsAsync(List<Expression> Expressions) {
            List<Instance> Results = new();
            foreach (Expression Expression in Expressions) {
                Results.Add(await InterpretExpressionAsync(Expression));
                if (Results[^1] is LoopControlReference or ReturnReference or StopReference) {
                    throw new SyntaxErrorException($"{Expression.Location}: Invalid {Results[^1].GetType().Name}");
                }
            }
            return Results;
        }
        internal async Task<Instance> InternalInterpretAsync(List<Expression> Statements, Method? OnYield = null) {
            try {
                // Set on yield
                CurrentOnYield = OnYield;
                // Interpret statements
                Instance LastInstance = Api.Nil;
                foreach (Expression Statement in Statements) {
                    LastInstance = await InterpretExpressionAsync(Statement);
                    if (LastInstance is LoopControlReference or ReturnReference or StopReference) {
                        break;
                    }
                }
                // Return last instance
                return LastInstance;
            }
            finally {
                // Reset on yield
                CurrentOnYield = null;
            }
        }
        internal async Task<Instance> InternalEvaluateAsync(string Code) {
            // Get statements from code
            List<Phase1.Phase1Token> Tokens = Phase1.GetPhase1Tokens(Code);
            List<Expression> Statements = ObjectsToExpressions(Tokens, ExpressionsType.Statements);

            // Interpret statements
            return await InternalInterpretAsync(Statements);
        }

        public async Task<Instance> InterpretAsync(List<Expression> Statements, Method? OnYield = null) {
            // Debounce
            if (Running) throw new ApiException("The script is already running.");
            Running = true;

            // Interpret statements and store the result
            Instance LastInstance;
            try {
                LastInstance = await InternalInterpretAsync(Statements, OnYield);
                if (LastInstance is LoopControlReference LoopControlReference) {
                    throw new SyntaxErrorException($"{ApproximateLocation}: Invalid {LoopControlReference.Type} (must be in a loop)");
                }
                else if (LastInstance is ReturnReference ReturnReference) {
                    return ReturnReference.ReturnValue;
                }
                else if (LastInstance is StopReference) {
                    return Api.Nil;
                }
            }
            finally {
                // Deactivate debounce
                Running = false;
            }
            return LastInstance;
        }
        public Instance Interpret(List<Expression> Statements) {
            return InterpretAsync(Statements).Result;
        }
        public async Task<Instance> EvaluateAsync(string Code) {
            List<Phase1.Phase1Token> Tokens = Phase1.GetPhase1Tokens(Code);
            List<Expression> Statements = ObjectsToExpressions(Tokens, ExpressionsType.Statements);

            /*Console.WriteLine(Statements.Inspect("\n"));
            Console.Write("Press enter to continue.");
            Console.ReadLine();*/

            return await InterpretAsync(Statements);
        }
        public Instance Evaluate(string Code) {
            return EvaluateAsync(Code).Result;
        }
        public async Task WaitForThreadsAsync() {
            HashSet<ScriptThread> CurrentScriptThreads = new(ScriptThreads);
            foreach (ScriptThread ScriptThread in CurrentScriptThreads) {
                if (ScriptThread.Running != null) {
                    await ScriptThread.Running;
                }
            }
        }
        public void WaitForThreads() {
            WaitForThreadsAsync().Wait();
        }
        /// <summary>Stops the script, including all running threads.</summary>
        public void Stop() {
            Stopping = true;
        }

        public Script(Interpreter interpreter, bool AllowUnsafeApi = true) {
            Interpreter = interpreter;
            this.AllowUnsafeApi = AllowUnsafeApi;

            CurrentObject.Push(Interpreter.RootModule);
            CurrentObject.Push(Interpreter.RootInstance);
            CurrentObject.Push(Interpreter.RootScope);
        }
    }
}
