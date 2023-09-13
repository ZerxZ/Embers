﻿using System.Runtime.CompilerServices;
using static Embers.Phase2;

#pragma warning disable CS1998

namespace Embers
{
    public class Script
    {
        public readonly Interpreter Interpreter;
        public readonly bool AllowUnsafeApi;
        public bool Running { get; private set; }
        public bool Stopping { get; private set; }
        public DebugLocation ApproximateLocation { get; private set; } = DebugLocation.Unknown;

        readonly Stack<object> CurrentObject = new();
        Block CurrentBlock => (Block)CurrentObject.First(obj => obj is Block);
        Scope CurrentScope => (Scope)CurrentObject.First(obj => obj is Scope);
        Module CurrentModule => (Module)CurrentObject.First(obj => obj is Module);
        Instance CurrentInstance => (Instance)CurrentObject.First(obj => obj is Instance);

        internal readonly ConditionalWeakTable<Exception, ExceptionInstance> ExceptionsTable = new();
        public int ThreadCount { get; private set; }

        public Instance CreateInstanceWithNew(Class Class) {
            if (Class.InheritsFrom(Interpreter.NilClass))
                return new NilInstance(Class);
            else if (Class.InheritsFrom(Interpreter.TrueClass))
                return new TrueInstance(Class);
            else if (Class.InheritsFrom(Interpreter.FalseClass))
                return new FalseInstance(Class);
            else if (Class.InheritsFrom(Interpreter.String))
                return new StringInstance(Class, "");
            else if (Class.InheritsFrom(Interpreter.Symbol))
                return new SymbolInstance(Class, "");
            else if (Class.InheritsFrom(Interpreter.Integer))
                return new IntegerInstance(Class, 0);
            else if (Class.InheritsFrom(Interpreter.Float))
                return new FloatInstance(Class, 0);
            else if (Class.InheritsFrom(Interpreter.Proc))
                throw new RuntimeException($"{ApproximateLocation}: Tried to create Proc instance without a block");
            else if (Class.InheritsFrom(Interpreter.Array))
                return new ArrayInstance(Class, new List<Instance>());
            else if (Class.InheritsFrom(Interpreter.Hash))
                return new HashInstance(Class, new Dictionary<Instance, Instance>(), Interpreter.Nil);
            else if (Class.InheritsFrom(Interpreter.Exception))
                return new ExceptionInstance(Class, "");
            else if (Class.InheritsFrom(Interpreter.Thread))
                return new ThreadInstance(Class, this);
            else
                return new Instance(Class);
        }

        public class Block {
            public readonly object? Parent;
            public readonly Dictionary<string, Instance> LocalVariables = new();
            public readonly Dictionary<string, Instance> Constants = new();
            public Block(object? parent) {
                Parent = parent;
            }
        }
        public class Scope : Block {
            public Scope(object? parent) : base(parent) { }
        }
        public class Module : Block {
            public readonly string Name;
            public readonly ReactiveDictionary<string, Method> Methods = new();
            public readonly ReactiveDictionary<string, Method> InstanceMethods = new();
            public readonly Dictionary<string, Instance> ClassVariables = new();
            public readonly Interpreter Interpreter;
            public readonly Module? SuperModule;
            public Module(string name, Module parent, Module? superModule = null) : base(parent) {
                Name = name;
                Interpreter = parent.Interpreter;
                SuperModule = superModule;
                Setup();
            }
            public Module(string name, Interpreter interpreter) : base(null) {
                Name = name;
                Interpreter = interpreter;
                SuperModule = null;
                Setup();
            }
            protected virtual void Setup() {
                // Copy superclass class and instance methods
                if (SuperModule != null) {
                    SuperModule.Methods.CopyTo(Methods);
                    SuperModule.InstanceMethods.CopyTo(InstanceMethods);
                    // Inherit changes later
                    SuperModule.Methods.Set += (string Key, Method NewValue) => {
                        Methods[Key] = NewValue;
                    };
                    SuperModule.InstanceMethods.Set += (string Key, Method NewValue) => {
                        InstanceMethods[Key] = NewValue;
                    };
                    SuperModule.Methods.Removed += (string Key) => {
                        Methods.Remove(Key);
                    };
                    SuperModule.InstanceMethods.Removed += (string Key) => {
                        InstanceMethods.Remove(Key);
                    };
                }
                // Copy default class and instance methods
                else {
                    Api.DefaultClassAndInstanceMethods.CopyTo(Methods);
                    Api.DefaultClassAndInstanceMethods.CopyTo(InstanceMethods);
                    Api.DefaultClassMethods.CopyTo(Methods);
                    Api.DefaultInstanceMethods.CopyTo(InstanceMethods);
                }
            }
            public bool InheritsFrom(Module Ancestor) {
                Module CurrentAncestor = this;
                while (true) {
                    if (CurrentAncestor == Ancestor)
                        return true;

                    if (CurrentAncestor.SuperModule is Module ModuleAncestor)
                        CurrentAncestor = ModuleAncestor;
                    else
                        return false;
                }
            }
        }
        public class Class : Module {
            public Class(string name, Module parent, Module? superClass = null) : base(name, parent, superClass) { }
            public Class(string name, Interpreter interpreter) : base(name, interpreter) { }
            protected override void Setup() {
                // Default method: new
                Methods["new"] = new Method(async Input => {
                    Instance NewInstance = Input.Script.CreateInstanceWithNew((Class)Input.Instance.Module!);
                    if (NewInstance.InstanceMethods.TryGetValue("initialize", out Method? Initialize)) {
                        // Call initialize & ignore result
                        await Input.Script.CreateTemporaryInstanceScope(NewInstance, async () => {
                            await Initialize.Call(Input.Script, NewInstance, Input.Arguments, Input.OnYield);
                        });
                        // Return instance
                        return NewInstance;
                    }
                    else {
                        throw new RuntimeException($"Undefined method 'initialize' for {Name}");
                    }
                }, null);
                // Default method: initialize
                InstanceMethods["initialize"] = new Method(async Input => {
                    return Input.Instance;
                }, 0);
                // Base setup
                base.Setup();
            }
        }
        public class Instance {
            /*public bool IsA<T>() {
                return GetType() == typeof(T);
            }*/
            public readonly Module? Module; // Will be null if instance is a pseudoinstance
            public readonly long ObjectId;
            public virtual ReactiveDictionary<string, Instance> InstanceVariables { get; } = new();
            public virtual ReactiveDictionary<string, Method> InstanceMethods { get; } = new();
            public bool IsTruthy => Object is not (null or false);
            public virtual object? Object { get { return null; } }
            public virtual bool Boolean { get { throw new RuntimeException("Instance is not a boolean"); } }
            public virtual string String { get { throw new RuntimeException("Instance is not a string"); } }
            public virtual long Integer { get { throw new RuntimeException("Instance is not an integer"); } }
            public virtual double Float { get { throw new RuntimeException("Instance is not a float"); } }
            public virtual Method Proc { get { throw new RuntimeException("Instance is not a proc"); } }
            public virtual ScriptThread? Thread { get { throw new RuntimeException("Instance is not a thread"); } }
            public virtual LongRange Range { get { throw new RuntimeException("Instance is not a range"); } }
            public virtual List<Instance> Array { get { throw new RuntimeException("Instance is not an array"); } }
            public virtual Dictionary<Instance, Instance> Hash { get { throw new RuntimeException("Instance is not a hash"); } }
            public virtual Exception Exception { get { throw new RuntimeException("Instance is not an exception"); } }
            public virtual Module ModuleRef { get { throw new ApiException("Instance is not a class/module reference"); } }
            public virtual Method MethodRef { get { throw new ApiException("Instance is not a method reference"); } }
            public virtual string Inspect() {
                return $"Instance of {Module?.Name}";
            }
            public virtual string LightInspect() {
                return Inspect();
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
                    return new StringInstance(Script.Interpreter.String, String);
                }

                return Token.Type switch {
                    Phase2TokenType.Nil => Script.Interpreter.Nil,
                    Phase2TokenType.True => Script.Interpreter.True,
                    Phase2TokenType.False => Script.Interpreter.False,
                    Phase2TokenType.String => new StringInstance(Script.Interpreter.String, Token.Value!),
                    Phase2TokenType.Integer => new IntegerInstance(Script.Interpreter.Integer, Token.ValueAsLong),
                    Phase2TokenType.Float => new FloatInstance(Script.Interpreter.Float, Token.ValueAsDouble),
                    _ => throw new InternalErrorException($"{Token.Location}: Cannot create new object from token type {Token.Type}")
                };
            }
            public Instance(Module fromModule) {
                Module = fromModule;
                ObjectId = fromModule.Interpreter.GenerateObjectId;
                Setup();
            }
            public Instance(Interpreter interpreter) {
                Module = null;
                ObjectId = interpreter.GenerateObjectId;
                Setup();
            }
            void Setup() {
                if (this is not PseudoInstance && Module != null) {
                    // Copy instance methods
                    Module.InstanceMethods.CopyTo(InstanceMethods);
                    // Inherit changes later
                    Module.InstanceMethods.Set += (string Key, Method NewValue) => {
                        InstanceMethods[Key] = NewValue;
                    };
                    Module.InstanceMethods.Removed += (string Key) => {
                        InstanceMethods.Remove(Key);
                    };
                }
            }
            public void AddOrUpdateInstanceMethod(string Name, Method Method) {
                lock (InstanceMethods) lock (Module!.InstanceMethods)
                    InstanceMethods[Name] =
                    Module.InstanceMethods[Name] = Method;
            }
            public async Task<Instance> TryCallInstanceMethod(Script Script, string MethodName, Instances? Arguments = null, Method? OnYield = null) {
                // Found
                if (InstanceMethods.TryGetValue(MethodName, out Method? FindMethod)) {
                    return await Script.CreateTemporaryClassScope(Module!, async () =>
                        await FindMethod.Call(Script, this, Arguments, OnYield)
                    );
                }
                // Error
                else {
                    throw new RuntimeException($"{Script.ApproximateLocation}: Undefined method '{MethodName}' for {Module?.Name}");
                }
            }
        }
        public class NilInstance : Instance {
            public override string Inspect() {
                return "nil";
            }
            public override string LightInspect() {
                return "";
            }
            public NilInstance(Class fromClass) : base(fromClass) { }
        }
        public class TrueInstance : Instance {
            public override object? Object { get { return true; } }
            public override bool Boolean { get { return true; } }
            public override string Inspect() {
                return "true";
            }
            public TrueInstance(Class fromClass) : base(fromClass) { }
        }
        public class FalseInstance : Instance {
            public override object? Object { get { return false; } }
            public override bool Boolean { get { return false; } }
            public override string Inspect() {
                return "false";
            }
            public FalseInstance(Class fromClass) : base(fromClass) { }
        }
        public class StringInstance : Instance {
            string Value;
            public override object? Object { get { return Value; } }
            public override string String { get { return Value; } }
            public override string Inspect() {
                return "\"" + Value.Replace("\n", "\\n").Replace("\r", "\\r") + "\"";
            }
            public override string LightInspect() {
                return Value;
            }
            public StringInstance(Class fromClass, string value) : base(fromClass) {
                Value = value;
            }
            public void SetValue(string value) {
                Value = value;
            }
        }
        public class SymbolInstance : Instance {
            string Value;
            public override object? Object { get { return Value; } }
            public override string String { get { return Value; } }

            bool IsStringSymbol;
            public override string Inspect() {
                if (IsStringSymbol) {
                    return ":\"" + Value.Replace("\n", "\\n").Replace("\r", "\\r") + "\"";
                }
                else {
                    return ":" + Value;
                }
            }
            public override string LightInspect() {
                return Value;
            }
            public SymbolInstance(Class fromClass, string value) : base(fromClass) {
                Value = value;
                SetValue(value);
            }
            public void SetValue(string value) {
                Value = value;
                IsStringSymbol = Value.Any("(){}[]<>=+-*/%!?.,;@#&|~^$_".Contains) || Value.Any(char.IsWhiteSpace) || (Value.Length != 0 && char.IsAsciiDigit(Value[0]));
            }
        }
        public class IntegerInstance : Instance {
            long Value;
            public override object? Object { get { return Value; } }
            public override long Integer { get { return Value; } }
            public override double Float { get { return Value; } }
            public override string Inspect() {
                return Value.ToString();
            }
            public IntegerInstance(Class fromClass, long value) : base(fromClass) {
                Value = value;
            }
            public void SetValue(long value) {
                Value = value;
            }
        }
        public class FloatInstance : Instance {
            double Value;
            public override object? Object { get { return Value; } }
            public override double Float { get { return Value; } }
            public override long Integer { get { return (long)Value; } }
            public override string Inspect() {
                string FloatString = Value.ToString();
                if (!FloatString.Contains('.')) {
                    FloatString += ".0";
                }
                return FloatString;
            }
            public FloatInstance(Class fromClass, double value) : base(fromClass) {
                Value = value;
            }
            public void SetValue(double value) {
                Value = value;
            }
        }
        public class ProcInstance : Instance {
            Method Value;
            public override object? Object { get { return Value; } }
            public override Method Proc { get { return Value; } }
            public override string Inspect() {
                return "ProcInstance";
            }
            public ProcInstance(Class fromClass, Method value) : base(fromClass) {
                Value = value;
            }
            public void SetValue(Method value) {
                Value = value;
            }
        }
        public class ThreadInstance : Instance {
            public readonly ScriptThread ScriptThread;
            public override object? Object { get { return ScriptThread; } }
            public override ScriptThread Thread { get { return ScriptThread; } }
            public override string Inspect() {
                return $"ThreadInstance";
            }
            public ThreadInstance(Class fromClass, Script fromScript) : base(fromClass) {
                ScriptThread = new(fromScript);
            }
            public void SetMethod(Method method) {
                Thread.Method = method;
            }
        }
        public class ScriptThread {
            public ThreadPhase Phase { get; private set; }
            public readonly Script FromScript;
            public Method? Method;
            public bool Stopping { get; private set; }
            public ScriptThread(Script fromScript) {
                FromScript = fromScript;
                Phase = ThreadPhase.Idle;
            }
            public async Task Run(Instances? Arguments = null) {
                // If already running, wait until it's finished
                if (Phase != ThreadPhase.Idle) {
                    while (Phase != ThreadPhase.Completed)
                        await Task.Delay(10);
                    return;
                }
                // Increase thread counter
                FromScript.ThreadCount++;
                try {
                    // Create a new script
                    Script ThreadScript = new(FromScript.Interpreter, FromScript.AllowUnsafeApi);
                    FromScript.CurrentObject.CopyTo(ThreadScript.CurrentObject);
                    Phase = ThreadPhase.Running;
                    // Run the method in the script
                    bool ThreadCompleted = false;
                    _ = Task.Run(async () => {
                        await Method!.Call(ThreadScript, null, Arguments);
                        ThreadCompleted = true;
                    });
                    // Wait for the script to finish
                    while (!FromScript.Stopping && !ThreadCompleted && !Stopping)
                        await Task.Delay(10);
                    // Stop the script
                    ThreadScript.Stop();
                    Phase = ThreadPhase.Completed;
                }
                finally {
                    // Decrease thread counter
                    FromScript.ThreadCount--;
                }
            }
            public void Stop() {
                Stopping = true;
            }
            public enum ThreadPhase {
                Idle,
                Running,
                Completed
            }
        }
        public class RangeInstance : Instance {
            public IntegerInstance? Min;
            public IntegerInstance? Max;
            public Instance AppliedMin;
            public Instance AppliedMax;
            public bool IncludesMax;
            public override object? Object { get { return ToLongRange; } }
            public override LongRange Range { get { return ToLongRange; } }
            public override string Inspect() {
                return $"{(Min != null ? Min.Inspect() : "")}{(IncludesMax ? ".." : "...")}{(Max != null ? Max.Inspect() : "")}";
            }
            public RangeInstance(Class fromClass, IntegerInstance? min, IntegerInstance? max, bool includesMax) : base(fromClass) {
                Min = min;
                Max = max;
                IncludesMax = includesMax;
                Setup();
            }
            public void SetValue(IntegerInstance min, IntegerInstance max, bool includesMax) {
                Min = min;
                Max = max;
                IncludesMax = includesMax;
                Setup();
            }
            void Setup() {
                if (Min == null) {
                    AppliedMin = Max!.Module!.Interpreter.Nil;
                    AppliedMax = IncludesMax ? Max : new IntegerInstance((Class)Max.Module!, Max.Integer - 1);
                }
                else if (Max == null) {
                    AppliedMin = Min;
                    AppliedMax = Min!.Module!.Interpreter.Nil;
                }
                else {
                    AppliedMin = Min;
                    AppliedMax = IncludesMax ? Max : new IntegerInstance((Class)Max.Module!, Max.Integer - 1);
                }
            }
            LongRange ToLongRange => new(AppliedMin is IntegerInstance ? AppliedMin.Integer : null, AppliedMax is IntegerInstance ? AppliedMax.Integer : null);
        }
        public class ArrayInstance : Instance {
            List<Instance> Value;
            public override object? Object { get { return Value; } }
            public override List<Instance> Array { get { return Value; } }
            public override string Inspect() {
                return $"[{Value.InspectInstances()}]";
            }
            public ArrayInstance(Class fromClass, List<Instance> value) : base(fromClass) {
                Value = value;
            }
            public void SetValue(List<Instance> value) {
                Value = value;
            }
        }
        public class HashInstance : Instance {
            Dictionary<Instance, Instance> Value;
            public Instance DefaultValue;
            public override object? Object { get { return Value; } }
            public override Dictionary<Instance, Instance> Hash { get { return Value; } }
            public override string Inspect() {
                return $"{{{Value.InspectInstances()}}}";
            }
            public HashInstance(Class fromClass, Dictionary<Instance, Instance> value, Instance defaultValue) : base(fromClass) {
                Value = value;
                DefaultValue = defaultValue;
            }
            public void SetValue(Dictionary<Instance, Instance> value, Instance defaultValue) {
                Value = value;
                DefaultValue = defaultValue;
            }
            public void SetValue(Dictionary<Instance, Instance> value) {
                Value = value;
            }
        }
        public class HashArgumentsInstance : Instance {
            public readonly HashInstance Value;
            public override string Inspect() {
                return $"Hash arguments instance: {{{Value.Inspect()}}}";
            }
            public HashArgumentsInstance(HashInstance value, Interpreter interpreter) : base(interpreter) {
                Value = value;
            }
        }
        public class ExceptionInstance : Instance {
            Exception Value;
            public override object? Object { get { return Value; } }
            public override Exception Exception { get { return Value; } }
            public override string Inspect() {
                return $"ExceptionInstance('{Value.Message}')";
            }
            public ExceptionInstance(Class fromClass, string message) : base(fromClass) {
                Value = new Exception(message);
            }
            public void SetValue(string message) {
                Value = new Exception(message);
            }
        }
        public abstract class PseudoInstance : Instance {
            public override ReactiveDictionary<string, Instance> InstanceVariables { get { throw new ApiException($"{GetType().Name} instance does not have instance variables"); } }
            public override ReactiveDictionary<string, Method> InstanceMethods { get { throw new ApiException($"{GetType().Name} instance does not have instance methods"); } }
            public PseudoInstance(Module module) : base(module) { }
            public PseudoInstance(Interpreter interpreter) : base(interpreter) { }
        }
        public class VariableReference : PseudoInstance {
            public Block? Block;
            public Instance? Instance;
            public Phase2Token Token;
            public bool IsLocalReference => Block == null && Instance == null;
            public override string Inspect() {
                return $"{(Block != null ? Block.GetType().Name : (Instance != null ? Instance.Inspect() : Token.Inspect()))} var ref in {Token.Inspect()}";
            }
            public VariableReference(Module module, Phase2Token token) : base(module) {
                Block = module;
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
        public class ModuleReference : PseudoInstance {
            public override object? Object { get { return Module; } }
            public override Module ModuleRef { get { return Module!; } }
            public override string Inspect() {
                return Module!.Name;
            }
            public override string LightInspect() {
                return Module!.Name;
            }
            public ModuleReference(Module module) : base(module) { }
        }
        public class MethodReference : PseudoInstance {
            readonly Method Method;
            public override object? Object { get { return Method; } }
            public override Method MethodRef { get { return Method; } }
            public override string Inspect() {
                return Method.ToString()!;
            }
            public MethodReference(Method method, Interpreter interpreter) : base(interpreter) {
                Method = method;
            }
        }
        public class Method {
            Func<MethodInput, Task<Instance>> Function;
            public readonly IntRange ArgumentCountRange;
            public readonly List<MethodArgumentExpression> ArgumentNames;
            public readonly bool Unsafe;
            public Method(Func<MethodInput, Task<Instance>> function, IntRange? argumentCountRange, List<MethodArgumentExpression>? argumentNames = null, bool IsUnsafe = false) {
                Function = function;
                ArgumentCountRange = argumentCountRange ?? new IntRange();
                ArgumentNames = argumentNames ?? new();
                Unsafe = IsUnsafe;
            }
            public Method(Func<MethodInput, Task<Instance>> function, Range argumentCountRange, List<MethodArgumentExpression>? argumentNames = null, bool IsUnsafe = false) {
                Function = function;
                ArgumentCountRange = new IntRange(argumentCountRange);
                ArgumentNames = argumentNames ?? new();
                Unsafe = IsUnsafe;
            }
            public Method(Func<MethodInput, Task<Instance>> function, int argumentCount, List<MethodArgumentExpression>? argumentNames = null, bool IsUnsafe = false) {
                Function = function;
                ArgumentCountRange = new IntRange(argumentCount, argumentCount);
                ArgumentNames = argumentNames ?? new();
                Unsafe = IsUnsafe;
            }
            public async Task<Instance> Call(Script Script, Instance? OnInstance, Instances? Arguments = null, Method? OnYield = null, BreakHandleType BreakHandleType = BreakHandleType.Invalid, bool CatchReturn = true) {
                if (Unsafe && !Script.AllowUnsafeApi)
                    throw new RuntimeException($"{Script.ApproximateLocation}: This method is unavailable since 'AllowUnsafeApi' is disabled for this script.");

                Arguments ??= new Instances();
                if (ArgumentCountRange.IsInRange(Arguments.Count)) {
                    // Create temporary scope
                    Script.CurrentObject.Push(new Scope(Script.CurrentBlock));
                    // Set argument variables
                    {
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
                                        && Arguments.MultiInstance.Last() is not HashArgumentsInstance)
                                    {
                                        SplatArguments.Add(Arguments[ArgumentIndex]);
                                        ArgumentIndex++;
                                    }
                                    // Create array from splat arguments
                                    ArrayInstance SplatArgumentsArray = new(Script.Interpreter.Array, SplatArguments);
                                    // Add array to scope
                                    Script.CurrentScope.LocalVariables.Add(ArgumentIdentifier, SplatArgumentsArray);
                                }
                                // Double splat argument
                                else if (ArgumentName.SplatType == SplatType.Double && Arguments.MultiInstance.Last() is HashArgumentsInstance DoubleSplatArgumentsHash) {
                                    // Add hash to scope
                                    Script.CurrentScope.LocalVariables.Add(ArgumentIdentifier, DoubleSplatArgumentsHash.Value);
                                }
                                // Normal argument
                                else {
                                    Script.CurrentScope.LocalVariables.Add(ArgumentIdentifier, Arguments[ArgumentIndex]);
                                }
                            }
                            // Optional argument not given
                            else {
                                Instance DefaultValue = ArgumentName.DefaultValue != null ? (await Script.InterpretExpressionAsync(ArgumentName.DefaultValue)) : Script.Interpreter.Nil;
                                Script.CurrentScope.LocalVariables.Add(ArgumentIdentifier, DefaultValue);
                            }
                            ArgumentNameIndex++;
                            ArgumentIndex++;
                        }
                    }
                    // Call method
                    Instance ReturnValue;
                    try {
                        ReturnValue = await Function(new MethodInput(Script, OnInstance, Arguments, OnYield));
                    }
                    catch (BreakException) {
                        if (BreakHandleType == BreakHandleType.Rethrow) {
                            throw;
                        }
                        else if (BreakHandleType == BreakHandleType.Destroy) {
                            ReturnValue = Script.Interpreter.Nil;
                        }
                        else {
                            throw new SyntaxErrorException($"{Script.ApproximateLocation}: Invalid break (break must be in a loop)");
                        }
                    }
                    catch (ReturnException Ex) when (CatchReturn) {
                        ReturnValue = Ex.Instance;
                    }
                    finally {
                        // Step back a scope
                        Script.CurrentObject.Pop();
                    }
                    // Return method return value
                    return ReturnValue;
                }
                else {
                    throw new RuntimeException($"{Script.ApproximateLocation}: Wrong number of arguments (given {Arguments.Count}, expected {ArgumentCountRange})");
                }
            }
            public void ChangeFunction(Func<MethodInput, Task<Instance>> function) {
                Function = function;
            }
        }
        public class MethodScope : Scope {
            public readonly Method? Method;
            public MethodScope(Block? parent, Method method) : base(parent) {
                Method = method;
            }
        }
        public class MethodInput {
            public Script Script;
            public Interpreter Interpreter;
            public Instance Instance;
            public Instances Arguments;
            public Method? OnYield;
            public MethodInput(Script script, Instance instance, Instances arguments, Method? onYield = null) {
                Script = script;
                Interpreter = script.Interpreter;
                Instance = instance;
                Arguments = arguments;
                OnYield = onYield;
            }
            public DebugLocation Location => Script.ApproximateLocation;
        }
        public class IntRange {
            public readonly int? Min;
            public readonly int? Max;
            public IntRange(int? min = null, int? max = null) {
                Min = min;
                Max = max;
            }
            public IntRange(Range range) {
                if (range.Start.IsFromEnd) {
                    Min = null;
                    Max = range.End.Value;
                }
                else if (range.End.IsFromEnd) {
                    Min = range.Start.Value;
                    Max = null;
                }
                else {
                    Min = range.Start.Value;
                    Max = range.End.Value;
                }
            }
            public bool IsInRange(int Number) {
                if (Min != null && Number < Min) return false;
                if (Max != null && Number > Max) return false;
                return true;
            }
            public override string ToString() {
                if (Min == Max) {
                    if (Min == null) {
                        return "any";
                    }
                    else {
                        return $"{Min}";
                    }
                }
                else {
                    if (Min == null)
                        return $"{Max}";
                    else if (Max == null)
                        return $"{Min}+";
                    else
                        return $"{Min}..{Max}";
                }
            }
            public string Serialise() {
                return $"new {typeof(IntRange).PathTo()}({(Min != null ? Min : "null")}, {(Max != null ? Max : "null")})";
            }
        }
        public class LongRange {
            public readonly long? Min;
            public readonly long? Max;
            public LongRange(long? min = null, long? max = null) {
                Min = min;
                Max = max;
            }
            public bool IsInRange(long Number) {
                if (Min != null && Number < Min) return false;
                if (Max != null && Number > Max) return false;
                return true;
            }
            public override string ToString() {
                if (Min == Max) {
                    if (Min == null) {
                        return "any";
                    }
                    else {
                        return $"{Min}";
                    }
                }
                else {
                    if (Min == null)
                        return $"{Max}";
                    else if (Max == null)
                        return $"{Min}+";
                    else
                        return $"{Min}..{Max}";
                }
            }
            public string Serialise() {
                return $"new {typeof(LongRange).PathTo()}({(Min != null ? Min : "null")}, {(Max != null ? Max : "null")})";
            }
        }
        public class WeakEvent<TDelegate> where TDelegate : class {
            readonly List<WeakReference> Subscribers = new();

            public void Add(TDelegate handler) {
                // Remove any dead references
                Subscribers.RemoveAll(WeakRef => !WeakRef.IsAlive);
                // Add the new handler as a weak reference
                Subscribers.Add(new WeakReference(handler));
            }

            public void Remove(TDelegate Handler) {
                // Remove the handler from the list
                Subscribers.RemoveAll(WeakRef => WeakRef.Target == Handler);
            }

            public void Raise(Action<TDelegate> Action) {
                // Invoke the action for each subscriber that is still alive
                foreach (WeakReference WeakRef in Subscribers) {
                    if (WeakRef.Target is TDelegate Target) {
                        Action(Target);
                    }
                }
            }
        }
        public class ReactiveDictionary<TKey, TValue> : Dictionary<TKey, TValue> where TKey : notnull {
            private readonly WeakEvent<DictionarySet> SetEvent = new();
            private readonly WeakEvent<DictionaryRemoved> RemovedEvent = new();

            public delegate void DictionarySet(TKey Key, TValue NewValue);
            public event DictionarySet Set {
                add => SetEvent.Add(value);
                remove => SetEvent.Remove(value);
            }

            public delegate void DictionaryRemoved(TKey Key);
            public event DictionaryRemoved Removed {
                add => RemovedEvent.Add(value);
                remove => RemovedEvent.Remove(value);
            }

            public new TValue this[TKey Key] {
                get => base[Key];
                set {
                    base[Key] = value;
                    SetEvent.Raise(handler => handler(Key, value));
                }
            }
            public new void Add(TKey Key, TValue Value) {
                base.Add(Key, Value);
                SetEvent.Raise(handler => handler(Key, Value));
            }
            public new bool Remove(TKey Key) {
                if (base.Remove(Key)) {
                    RemovedEvent.Raise(handler => handler(Key));
                    return true;
                }
                return false;
            }
        }
        public class Instances {
            // At least one of Instance or InstanceList will be null
            readonly Instance? Instance;
            readonly List<Instance>? InstanceList;
            public readonly int Count;

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
                if (Instances.Count == 0)
                    throw new RuntimeException($"Cannot implicitly cast Instances to Instance because there are none.");
                if (Instances.Count != 1)
                    throw new RuntimeException($"Cannot implicitly cast Instances to Instance because {Instances.Count - 1} instances would be overlooked");
                return Instances[0];
            }
            public Instance this[int i] => InstanceList != null ? InstanceList[i] : (i == 0 && Instance != null ? Instance : throw new ApiException("Index was outside the range of the instances"));
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
            public Instance SingleInstance { get {
                if (Count == 1) {
                    return this[0];
                }
                else {
                    throw new SyntaxErrorException($"Unexpected instances (expected one, got {Count})");
                }
            } }
            public List<Instance> MultiInstance { get {
                if (InstanceList != null) {
                    return InstanceList;
                }
                else if (Instance != null) {
                    return new List<Instance>() { Instance };
                }
                else {
                    return new List<Instance>();
                }
            } }
        }

        public async Task Warn(string Message) {
            await Interpreter.RootInstance.InstanceMethods["warn"].Call(this, new ModuleReference(Interpreter.RootModule), new StringInstance(Interpreter.String, Message));
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
        T CreateTemporaryClassScope<T>(Module Module, Func<T> Do) {
            // Create temporary class/module scope
            CurrentObject.Push(Module);
            // Do action
            T Result = Do();
            // Step back a class/module
            CurrentObject.Pop();
            // Return result
            return Result;
        }
        T CreateTemporaryInstanceScope<T>(Instance Instance, Func<T> Do) {
            // Create temporary instance scope
            CurrentObject.Push(Instance);
            // Do action
            T Result = Do();
            // Step back an instance
            CurrentObject.Pop();
            // Return result
            return Result;
        }
        public SymbolInstance GetSymbol(string Value) {
            if (Interpreter.Symbols.TryGetValue(Value, out SymbolInstance? FindSymbolInstance)) {
                return FindSymbolInstance;
            }
            else {
                SymbolInstance SymbolInstance = new(Interpreter.Symbol, Value);
                Interpreter.Symbols[Value] = SymbolInstance;
                return SymbolInstance;
            }
        }
        public bool TryGetLocalVariable(string Name, out Instance? LocalVariable) {
            IEnumerable<Block> CurrentBlockStack = CurrentObject.Where(obj => obj is Block).Cast<Block>();

            foreach (Block Block in CurrentBlockStack) {
                if (Block.LocalVariables.TryGetValue(Name, out Instance? FindLocalVariable)) {
                    LocalVariable = FindLocalVariable;
                    return true;
                }
            }
            LocalVariable = null;
            return false;
        }
        public bool TryGetLocalConstant(string Name, out Instance? LocalConstant) {
            IEnumerable<Block> CurrentBlockStack = CurrentObject.Where(obj => obj is Block).Cast<Block>();

            foreach (Block Block in CurrentBlockStack) {
                if (Block.Constants.TryGetValue(Name, out Instance? FindLocalConstant)) {
                    LocalConstant = FindLocalConstant;
                    return true;
                }
            }
            LocalConstant = null;
            return false;
        }
        public bool TryGetLocalInstanceMethod(string Name, out Method? LocalInstanceMethod) {
            IEnumerable<Instance> CurrentInstanceStack = CurrentObject.Where(obj => obj is Instance).Cast<Instance>();

            foreach (Instance Instance in CurrentInstanceStack) {
                if (Instance.InstanceMethods.TryGetValue(Name, out Method? FindLocalInstanceMethod)) {
                    LocalInstanceMethod = FindLocalInstanceMethod;
                    return true;
                }
            }
            LocalInstanceMethod = null;
            return false;
        }

        async Task<Instance> InterpretMethodCallExpression(MethodCallExpression MethodCallExpression) {
            Instance MethodPath = await InterpretExpressionAsync(MethodCallExpression.MethodPath, ReturnType.FoundVariable);
            if (MethodPath is VariableReference MethodReference) {
                // Static method
                if (MethodReference.Block != null) {
                    // Get class/module which owns method
                    Module MethodModule = MethodReference.Block as Module ?? CurrentModule;
                    // Get instance of the class/module which owns method
                    Instance MethodOwner;
                    if (MethodCallExpression.MethodPath is PathExpression MethodCallPathExpression) {
                        MethodOwner = await InterpretExpressionAsync(MethodCallPathExpression.ParentObject);
                    }
                    else {
                        MethodOwner = new ModuleReference(MethodModule);
                    }
                    // Call class method
                    bool Found = MethodModule.Methods.TryGetValue(MethodReference.Token.Value!, out Method? StaticMethod);
                    if (Found) {
                        return await CreateTemporaryClassScope(MethodModule, async () =>
                            await StaticMethod!.Call(
                                this, MethodOwner, await InterpretExpressionsAsync(MethodCallExpression.Arguments), MethodCallExpression.OnYield?.Method
                            )
                        );
                    }
                    else {
                        throw new RuntimeException($"{MethodReference.Token.Location}: Undefined method '{MethodReference.Token.Value!}' for {CurrentInstance.Module!.Name}");
                    }
                }
                // Instance method
                else {
                    // Local
                    if (MethodReference.IsLocalReference) {
                        // Call local instance method
                        bool Found = TryGetLocalInstanceMethod(MethodReference.Token.Value!, out Method? LocalInstanceMethod);
                        if (Found) {
                            return await CreateTemporaryInstanceScope(CurrentInstance, async () =>
                                await LocalInstanceMethod!.Call(
                                    this, CurrentInstance, await InterpretExpressionsAsync(MethodCallExpression.Arguments), MethodCallExpression.OnYield?.Method
                                )
                            );
                        }
                        else {
                            throw new RuntimeException($"{MethodReference.Token.Location}: Undefined method '{MethodReference.Token.Value!}'");
                        }
                    }
                    // Path
                    else {
                        Instance MethodInstance = MethodReference.Instance!;
                        // Call instance method
                        bool Found = MethodInstance.InstanceMethods.TryGetValue(MethodReference.Token.Value!, out Method? PathInstanceMethod);
                        if (Found) {
                            return await CreateTemporaryInstanceScope(MethodInstance, async () =>
                                await PathInstanceMethod!.Call(
                                    this, MethodInstance, await InterpretExpressionsAsync(MethodCallExpression.Arguments), MethodCallExpression.OnYield?.Method
                                )
                            );
                        }
                        else {
                            throw new RuntimeException($"{MethodReference.Token.Location}: Undefined method '{MethodReference.Token.Value!}' for {CurrentInstance.Module!.Name}");
                        }
                    }
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
                // Static method
                if (ParentInstance is ModuleReference ParentModule) {
                    // Method
                    if (ReturnType != ReturnType.HypotheticalVariable) {
                        // Found
                        if (ParentModule.Module!.Methods.TryGetValue(PathExpression.Token.Value!, out Method? FindMethod)) {
                            // Call class/module method
                            if (ReturnType == ReturnType.InterpretResult) {
                                return await CreateTemporaryClassScope(ParentModule.Module, async () =>
                                    await FindMethod.Call(this, ParentModule)
                                );
                            }
                            // Return method
                            else {
                                return new VariableReference(ParentModule.Module, PathExpression.Token);
                            }
                        }
                        // Error
                        else {
                            throw new RuntimeException($"{PathExpression.Token.Location}: Undefined method '{PathExpression.Token.Value!}' for {ParentModule.Module.Name}");
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
                        // Method
                        if (ParentInstance.InstanceMethods.TryGetValue(PathExpression.Token.Value!, out Method? FindMethod)) {
                            // Call instance method
                            if (ReturnType == ReturnType.InterpretResult) {
                                return await CreateTemporaryInstanceScope(ParentInstance, async () =>
                                    await FindMethod.Call(this, ParentInstance)
                                );
                            }
                            // Return method
                            else {
                                return new VariableReference(ParentInstance, PathExpression.Token);
                            }
                        }
                        // Error
                        else {
                            throw new RuntimeException($"{PathExpression.Token.Location}: Undefined method '{PathExpression.Token.Value!}' for {ParentInstance.Inspect()}");
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
                                    throw new RuntimeException($"{ObjectTokenExpression.Token.Location}: Undefined local variable or method '{ObjectTokenExpression.Token.Value!}' for {CurrentScope}");
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
                                    return Interpreter.Nil;
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
                                    return Interpreter.Nil;
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
                            // Symbol
                            case Phase2TokenType.Symbol: {
                                return GetSymbol(ObjectTokenExpression.Token.Value!);
                            }
                            // Self
                            case Phase2TokenType.Self: {
                                return new ModuleReference(CurrentModule);
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
                return await InternalInterpretAsync(IfExpression.Statements);
            }
            return Interpreter.Nil;
        }
        async Task<ArrayInstance> InterpretArrayExpression(ArrayExpression ArrayExpression) {
            List<Instance> Items = new();
            foreach (Expression Item in ArrayExpression.Expressions) {
                Items.Add(await InterpretExpressionAsync(Item));
            }
            return new ArrayInstance(Interpreter.Array, Items);
        }
        async Task<HashInstance> InterpretHashExpression(HashExpression HashExpression) {
            Dictionary<Instance, Instance> Items = new();
            foreach (KeyValuePair<Expression, Expression> Item in HashExpression.Expressions) {
                Items.Add(await InterpretExpressionAsync(Item.Key), await InterpretExpressionAsync(Item.Value));
            }
            return new HashInstance(Interpreter.Hash, Items, Interpreter.Nil);
        }
        async Task<Instance> InterpretWhileExpression(WhileExpression WhileExpression) {
            while ((await InterpretExpressionAsync(WhileExpression.Condition!)).IsTruthy != WhileExpression.Inverse) {
                try {
                    await InternalInterpretAsync(WhileExpression.Statements);
                }
                catch (BreakException) {
                    break;
                }
                catch (RetryException) {
                    throw new SyntaxErrorException($"{ApproximateLocation}: Retry not valid in while loop");
                }
                catch (RedoException) {
                    continue;
                }
                catch (NextException) {
                    continue;
                }
                catch (LoopControlException Ex) {
                    throw new SyntaxErrorException($"{ApproximateLocation}: {Ex.GetType().Name} not valid in while loop");
                }
            }
            return Interpreter.Nil;
        }
        async Task<Instance> InterpretWhileStatement(WhileStatement WhileStatement) {
            try {
                // Create scope
                CurrentObject.Push(new Scope(CurrentObject));
                // Run statements
                await InterpretExpressionAsync(WhileStatement.WhileExpression);
            }
            finally {
                // Step back a scope
                CurrentObject.Pop();
            }
            //
            return Interpreter.Nil;
        }
        async Task<Instance> InterpretForStatement(ForStatement ForStatement) {
            Instance InResult = await InterpretExpressionAsync(ForStatement.InExpression);
            if (InResult.InstanceMethods.TryGetValue("each", out Method? EachMethod)) {
                await EachMethod.Call(this, InResult, OnYield: ForStatement.BlockStatementsMethod);
            }
            else {
                throw new RuntimeException($"{ForStatement.Location}: The instance must have an 'each' method to iterate with 'for'");
            }
            return Interpreter.Nil;
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
                        return Interpreter.False;
                default:
                    throw new InternalErrorException($"{LogicalExpression.Location}: Unhandled logical expression type: '{LogicalExpression.LogicType}'");
            }
        }
        async Task<Instance> InterpretNotExpression(NotExpression NotExpression) {
            Instance Right = await InterpretExpressionAsync(NotExpression.Right);
            return Right.IsTruthy ? Interpreter.False : Interpreter.True;
        }
        async Task<Instance> InterpretDefineMethodStatement(DefineMethodStatement DefineMethodStatement) {
            Instance MethodNameObject = await InterpretExpressionAsync(DefineMethodStatement.MethodName, ReturnType.HypotheticalVariable);
            if (MethodNameObject is VariableReference MethodNameRef) {
                string MethodName = MethodNameRef.Token.Value!;
                // Define static method
                if (MethodNameRef.Block != null) {
                    Module MethodModule = (Module)MethodNameRef.Block;
                    // Prevent redefining unsafe API methods
                    if (!AllowUnsafeApi && MethodModule.Methods.TryGetValue(MethodName, out Method? ExistingMethod) && ExistingMethod.Unsafe) {
                        throw new RuntimeException($"{DefineMethodStatement.Location}: The static method '{MethodName}' cannot be redefined since 'AllowUnsafeApi' is disabled for this script.");
                    }
                    // Create or overwrite static method
                    lock (MethodModule.Methods)
                        MethodModule.Methods[MethodName] = DefineMethodStatement.MethodExpression.Method;
                }
                // Define instance method
                else {
                    Instance MethodInstance = MethodNameRef.Instance ?? CurrentInstance;
                    // Prevent redefining unsafe API methods
                    if (!AllowUnsafeApi && MethodInstance.InstanceMethods.TryGetValue(MethodName, out Method? ExistingMethod) && ExistingMethod.Unsafe) {
                        throw new RuntimeException($"{DefineMethodStatement.Location}: The instance method '{MethodName}' cannot be redefined since 'AllowUnsafeApi' is disabled for this script.");
                    }
                    // Create or overwrite instance method
                    MethodInstance.AddOrUpdateInstanceMethod(MethodName, DefineMethodStatement.MethodExpression.Method);
                }
            }
            else {
                throw new InternalErrorException($"{DefineMethodStatement.Location}: Invalid method name: {MethodNameObject}");
            }
            return Interpreter.Nil;
        }
        async Task<Instance> InterpretDefineClassStatement(DefineClassStatement DefineClassStatement) {
            Instance ClassNameObject = await InterpretExpressionAsync(DefineClassStatement.ClassName, ReturnType.HypotheticalVariable);
            if (ClassNameObject is VariableReference ClassNameRef) {
                string ClassName = ClassNameRef.Token.Value!;
                Module? InheritsFrom = DefineClassStatement.InheritsFrom != null ? (await InterpretExpressionAsync(DefineClassStatement.InheritsFrom)).Module : null;

                // Create or patch class
                Module NewModule;
                // Patch class
                if (CurrentModule.Constants.TryGetValue(ClassName, out Instance? ConstantValue) && ConstantValue is ModuleReference ModuleReference) {
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
                            NewModule = CreateModule(ClassName, null, InheritsFrom);
                        }
                    }
                    else {
                        if (ClassNameRef.Module != null) {
                            NewModule = CreateClass(ClassName, ClassNameRef.Module, InheritsFrom);
                        }
                        else {
                            NewModule = CreateClass(ClassName, null, InheritsFrom);
                        }
                    }
                }

                // Interpret class statements
                await CreateTemporaryClassScope(NewModule, async () => {
                    await CreateTemporaryInstanceScope(new Instance(NewModule), async () => {
                        await InternalInterpretAsync(DefineClassStatement.BlockStatements);
                    });
                });

                // Store class/module constant
                if (ClassNameRef.Block != null) {
                    // Path
                    Module Module = (Module)ClassNameRef.Block;
                    lock (Module.Constants)
                        Module.Constants[ClassName] = new ModuleReference(NewModule);
                }
                else if (ClassNameRef.IsLocalReference) {
                    // Local
                    Module Module = (ClassNameRef.Instance ?? CurrentInstance).Module!;
                    lock (Module)
                        Module.Constants[ClassName] = new ModuleReference(NewModule);
                }
            }
            else {
                throw new InternalErrorException($"{DefineClassStatement.Location}: Invalid class/module name: {ClassNameObject}");
            }
            return Interpreter.Nil;
        }
        async Task<Instance> InterpretYieldStatement(YieldStatement YieldStatement, Method? OnYield) {
            if (OnYield != null) {
                List<Instance> YieldArgs = YieldStatement.YieldValues != null
                    ? await InterpretExpressionsAsync(YieldStatement.YieldValues)
                    : new();
                await OnYield.Call(this, null, YieldArgs, BreakHandleType: BreakHandleType.Destroy, CatchReturn: false);
            }
            else {
                throw new RuntimeException($"{YieldStatement.Location}: No block given to yield to");
            }
            return Interpreter.Nil;
        }
        async Task<Instance> InterpretRangeExpression(RangeExpression RangeExpression) {
            Instance? RawMin = null;
            if (RangeExpression.Min != null) RawMin = await InterpretExpressionAsync(RangeExpression.Min);
            Instance? RawMax = null;
            if (RangeExpression.Max != null) RawMax = await InterpretExpressionAsync(RangeExpression.Max);

            if (RawMin is IntegerInstance Min && RawMax is IntegerInstance Max) {
                return new RangeInstance(Interpreter.Range, Min, Max, RangeExpression.IncludesMax);
            }
            else if (RawMin == null && RawMax is IntegerInstance MaxOnly) {
                return new RangeInstance(Interpreter.Range, null, MaxOnly, RangeExpression.IncludesMax);
            }
            else if (RawMax == null && RawMin is IntegerInstance MinOnly) {
                return new RangeInstance(Interpreter.Range, MinOnly, null, RangeExpression.IncludesMax);
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
                        Instance LastInstance;
                        try {
                            // Create scope
                            CurrentObject.Push(new Scope(CurrentObject));
                            // Run statements
                            LastInstance = await InternalInterpretAsync(Branch.Statements);
                        }
                        finally {
                            // Step back a scope
                            CurrentObject.Pop();
                        }
                        return LastInstance;
                    }
                }
                // Else
                else {
                    Instance LastInstance;
                    try {
                        // Create scope
                        CurrentObject.Push(new Scope(CurrentObject));
                        // Run statements
                        LastInstance = await InternalInterpretAsync(Branch.Statements);
                    }
                    finally {
                        // Step back a scope
                        CurrentObject.Pop();
                    }
                    return LastInstance;
                }
            }
            return Interpreter.Nil;
        }
        async Task<Instance> InterpretBeginBranchesStatement(BeginBranchesStatement BeginBranchesStatement) {
            // Begin
            BeginStatement BeginBranch = (BeginStatement)BeginBranchesStatement.Branches[0];
            Exception? ExceptionToRescue = null;
            try {
                // Create scope
                CurrentObject.Push(new Scope(CurrentObject));
                // Run statements
                await InternalInterpretAsync(BeginBranch.Statements);
            }
            catch (Exception Ex) when (Ex is not (ExitException or LoopControlException)) {
                ExceptionToRescue = Ex;
            }
            finally {
                // Step back a scope
                CurrentObject.Pop();
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
                        ExceptionInstance ??= new(Interpreter.RuntimeError, ExceptionToRescue.Message);
                        // Get the rescuing exception type
                        Module RescuingExceptionModule = RescueStatement.Exception != null
                            ? (await InterpretExpressionAsync(RescueStatement.Exception)).Module!
                            : Interpreter.StandardError;

                        // Check whether rescue applies to this exception
                        bool CanRescue = false;
                        if (ExceptionInstance.Module!.InheritsFrom(RescuingExceptionModule)) {
                            CanRescue = true;
                        }

                        // Run the statements in the rescue block
                        if (CanRescue) {
                            Rescued = true;
                            try {
                                // Create scope
                                CurrentObject.Push(new Scope(CurrentObject));
                                // Set exception variable to exception instance
                                if (RescueStatement.ExceptionVariable != null) {
                                    CurrentScope.LocalVariables[RescueStatement.ExceptionVariable.Value!] = ExceptionInstance;
                                }
                                // Run statements
                                await InternalInterpretAsync(RescueStatement.Statements);
                            }
                            finally {
                                // Step back a scope
                                CurrentObject.Pop();
                            }
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
                    try {
                        // Create scope
                        CurrentObject.Push(new Scope(CurrentObject));
                        // Run statements
                        await InternalInterpretAsync(Branch.Statements);
                    }
                    finally {
                        // Step back a scope
                        CurrentObject.Pop();
                    }
                }
            }

            return Interpreter.Nil;
        }
        async Task<Instance> InterpretAssignmentExpression(AssignmentExpression AssignmentExpression, ReturnType ReturnType) {
            async Task AssignToVariable(VariableReference Variable, Instance Value) {
                switch (Variable.Token.Type) {
                    case Phase2TokenType.LocalVariableOrMethod:
                        // call variable=
                        if (Variable.Instance != null) {
                            await Variable.Instance.TryCallInstanceMethod(this, Variable.Token.Value! + "=", Value);
                        }
                        // set variable =
                        else {
                            lock (CurrentBlock.LocalVariables)
                                CurrentBlock.LocalVariables[Variable.Token.Value!] = Value;
                        }
                        break;
                    case Phase2TokenType.GlobalVariable:
                        lock (Interpreter.GlobalVariables)
                            Interpreter.GlobalVariables[Variable.Token.Value!] = Value;
                        break;
                    case Phase2TokenType.ConstantOrMethod:
                        if (CurrentBlock.Constants.ContainsKey(Variable.Token.Value!))
                            await Warn($"{Variable.Token.Location}: Already initialized constant '{Variable.Token.Value!}'");
                        lock (CurrentBlock.Constants)
                            CurrentBlock.Constants[Variable.Token.Value!] = Value;
                        break;
                    case Phase2TokenType.InstanceVariable:
                        lock (CurrentInstance.InstanceVariables)
                            CurrentInstance.InstanceVariables[Variable.Token.Value!] = Value;
                        break;
                    case Phase2TokenType.ClassVariable:
                        lock (CurrentModule.ClassVariables)
                            CurrentModule.ClassVariables[Variable.Token.Value!] = Value;
                        break;
                    default:
                        throw new InternalErrorException($"{Variable.Token.Location}: Assignment variable token is not a variable type (got {Variable.Token.Type})");
                }
            }

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
        async Task<Instance> InterpretUndefineMethodStatement(UndefineMethodStatement UndefineMethodStatement) {
            string MethodName = UndefineMethodStatement.MethodName.Token.Value!;
            if (MethodName == "initialize") {
                await Warn($"{UndefineMethodStatement.MethodName.Token.Location}: undefining 'initialize' may cause serious problems");
            }
            if (!CurrentModule.InstanceMethods.Remove(MethodName)) {
                throw new RuntimeException($"{UndefineMethodStatement.MethodName.Token.Location}: Undefined method '{MethodName}' for {CurrentModule.Name}");
            }
            return Interpreter.Nil;
        }
        async Task<Instance> InterpretDefinedExpression(DefinedExpression DefinedExpression) {
            if (DefinedExpression.Expression is MethodCallExpression || DefinedExpression.Expression is PathExpression) {
                return new StringInstance(Interpreter.String, "method");
            }
            else if (DefinedExpression.Expression is ObjectTokenExpression ObjectToken) {
                if (ObjectToken.Token.Type == Phase2TokenType.LocalVariableOrMethod) {
                    if (CurrentScope.LocalVariables.ContainsKey(ObjectToken.Token.Value!)) {
                        return new StringInstance(Interpreter.String, "local-variable");
                    }
                    else if (CurrentInstance.InstanceMethods.ContainsKey(ObjectToken.Token.Value!)) {
                        return new StringInstance(Interpreter.String, "method");
                    }
                    else {
                        return Interpreter.Nil;
                    }
                }
                else if (ObjectToken.Token.Type == Phase2TokenType.GlobalVariable) {
                    if (Interpreter.GlobalVariables.ContainsKey(ObjectToken.Token.Value!)) {
                        return new StringInstance(Interpreter.String, "global-variable");
                    }
                    else {
                        return Interpreter.Nil;
                    }
                }
                else if (ObjectToken.Token.Type == Phase2TokenType.ConstantOrMethod) {
                    throw new NotImplementedException("Defined? not yet implemented for constants");
                }
                else if (ObjectToken.Token.Type == Phase2TokenType.InstanceVariable) {
                    throw new NotImplementedException("Defined? not yet implemented for instance variables");
                }
                else if (ObjectToken.Token.Type == Phase2TokenType.ClassVariable) {
                    if (CurrentModule.ClassVariables.ContainsKey(ObjectToken.Token.Value!)) {
                        return new StringInstance(Interpreter.String, "class-variable");
                    }
                    else {
                        return Interpreter.Nil;
                    }
                }
                else {
                    return new StringInstance(Interpreter.String, "expression");
                }
            }
            else {
                throw new InternalErrorException($"{DefinedExpression.Location}: Unknown expression type for defined?: {DefinedExpression.Expression.GetType().Name}");
            }
        }
        async Task<Instance> InterpretHashArgumentsExpression(HashArgumentsExpression HashArgumentsExpression) {
            return new HashArgumentsInstance(
                await InterpretHashExpression(HashArgumentsExpression.HashExpression),
                Interpreter
            );
        }
        async Task<Instance> InterpretEnvironmentInfoExpression(EnvironmentInfoExpression EnvironmentInfoExpression) {
            if (EnvironmentInfoExpression.Type == EnvironmentInfoType.__LINE__) {
                return new IntegerInstance(Interpreter.Integer, ApproximateLocation.Line);
            }
            else {
                throw new InternalErrorException($"{ApproximateLocation}: Environment info type not handled: '{EnvironmentInfoExpression.Type}'");
            }
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
        async Task<Instance> InterpretExpressionAsync(Expression Expression, ReturnType ReturnType = ReturnType.InterpretResult, Method? OnYield = null) {
            // Set approximate location
            ApproximateLocation = Expression.Location;

            // Stop script
            if (Stopping)
                throw new StopException();

            // Interpret expression
            return Expression switch {
                MethodCallExpression MethodCallExpression => await InterpretMethodCallExpression(MethodCallExpression),
                ObjectTokenExpression ObjectTokenExpression => await InterpretObjectTokenExpression(ObjectTokenExpression, ReturnType),
                IfExpression IfExpression => await InterpretIfExpression(IfExpression),
                WhileExpression WhileExpression => await InterpretWhileExpression(WhileExpression),
                ArrayExpression ArrayExpression => await InterpretArrayExpression(ArrayExpression),
                HashExpression HashExpression => await InterpretHashExpression(HashExpression),
                WhileStatement WhileStatement => await InterpretWhileStatement(WhileStatement),
                ForStatement ForStatement => await InterpretForStatement(ForStatement),
                SelfExpression => new ModuleReference(CurrentModule),
                LogicalExpression LogicalExpression => await InterpretLogicalExpression(LogicalExpression),
                NotExpression NotExpression => await InterpretNotExpression(NotExpression),
                DefineMethodStatement DefineMethodStatement => await InterpretDefineMethodStatement(DefineMethodStatement),
                DefineClassStatement DefineClassStatement => await InterpretDefineClassStatement(DefineClassStatement),
                ReturnStatement ReturnStatement => throw new ReturnException(
                                                        ReturnStatement.ReturnValue != null
                                                        ? await InterpretExpressionAsync(ReturnStatement.ReturnValue)
                                                        : Interpreter.Nil),
                LoopControlStatement LoopControlStatement => LoopControlStatement.Type switch {
                    LoopControlType.Break => throw new BreakException(),
                    LoopControlType.Retry => throw new RetryException(),
                    LoopControlType.Redo => throw new RedoException(),
                    LoopControlType.Next => throw new NextException(),
                    _ => throw new InternalErrorException($"{Expression.Location}: Loop control type not handled: '{LoopControlStatement.Type}'") },
                YieldStatement YieldStatement => await InterpretYieldStatement(YieldStatement, OnYield),
                RangeExpression RangeExpression => await InterpretRangeExpression(RangeExpression),
                IfBranchesStatement IfBranchesStatement => await InterpretIfBranchesStatement(IfBranchesStatement),
                BeginBranchesStatement BeginBranchesStatement => await InterpretBeginBranchesStatement(BeginBranchesStatement),
                AssignmentExpression AssignmentExpression => await InterpretAssignmentExpression(AssignmentExpression, ReturnType),
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
            }
            return Results;
        }
        internal async Task<Instance> InternalInterpretAsync(List<Expression> Statements, Method? OnYield = null) {
            // Interpret statements
            Instance LastInstance = Interpreter.Nil;
            for (int Index = 0; Index < Statements.Count; Index++) {
                // Interpret expression and store the result
                Expression Statement = Statements[Index];
                LastInstance = await InterpretExpressionAsync(Statement, OnYield: OnYield);
            }
            // Return last expression
            return LastInstance;
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
            }
            catch (LoopControlException Ex) {
                throw new SyntaxErrorException($"{ApproximateLocation}: Invalid {Ex.GetType().Name} (must be in a loop)");
            }
            catch (ReturnException Ex) {
                return Ex.Instance;
            }
            catch (ExitException) {
                return Interpreter.Nil;
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

            /*Console.WriteLine(Statements.Inspect());
            Console.Write("Press enter to continue.");
            Console.ReadLine();*/

            return await InterpretAsync(Statements);
        }
        public Instance Evaluate(string Code) {
            return EvaluateAsync(Code).Result;
        }
        public async Task WaitForThreadsAsync() {
            while (ThreadCount > 0)
                await Task.Delay(10);
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

            CurrentObject.Push(interpreter.RootModule);
            CurrentObject.Push(interpreter.RootInstance);
            CurrentObject.Push(interpreter.RootScope);
        }
    }
}
