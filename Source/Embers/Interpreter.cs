﻿using System;
using System.Collections.Generic;
using static Embers.Phase2;
using static Embers.Script;

namespace Embers
{
    public class Interpreter
    {
        public readonly Class Object; // The superclass of all classes and modules.
        public readonly Module RootModule;
        public readonly Instance RootInstance;
        public readonly Scope RootScope;

        public readonly LockingDictionary<string, Instance> GlobalVariables = new();
        public readonly LockingDictionary<string, SymbolInstance> Symbols = new();

        public readonly Class NilClass;
        public readonly Class TrueClass;
        public readonly Class FalseClass;
        public readonly Class String;
        public readonly Class Symbol;
        public readonly Class Integer;
        public readonly Class Float;
        public readonly Class Proc;
        public readonly Class Range;
        public readonly Class Array;
        public readonly Class Hash;
        public readonly Class Exception;
        public readonly Class StandardError;
        public readonly Class RuntimeError;
        public readonly Class Thread;
        public readonly Class Time;

        public readonly NilInstance Nil;
        public readonly TrueInstance True;
        public readonly FalseInstance False;

        public readonly Random InternalRandom = new();
        public long RandomSeed;
        public Random Random;
        public long GenerateObjectId => InternalNextObjectId++;
        private long InternalNextObjectId = 0;
        
        public static string Serialise(string Code) {
            List<Phase1.Phase1Token> Tokens = Phase1.GetPhase1Tokens(Code);
            List<Expression> Statements = ObjectsToExpressions(Tokens, ExpressionsType.Statements);

            return Statements.Serialise();
        }

        public Interpreter() {
            Object = new Class("Object", this); Object.InstanceMethods.Remove("initialize"); Object.Methods.Remove("new");
            RootModule = new Module("main", this, Object);
            RootInstance = new Instance(RootModule);
            RootScope = new Scope();

            Script MainScript = new(this);

            NilClass = MainScript.CreateClass("NilClass"); Nil = new NilInstance(NilClass); NilClass.InstanceMethods.Remove("initialize"); NilClass.Methods.Remove("new");
            TrueClass = MainScript.CreateClass("TrueClass"); True = new TrueInstance(TrueClass); TrueClass.InstanceMethods.Remove("initialize"); TrueClass.Methods.Remove("new");
            FalseClass = MainScript.CreateClass("FalseClass"); False = new FalseInstance(FalseClass); FalseClass.InstanceMethods.Remove("initialize"); FalseClass.Methods.Remove("new");
            String = MainScript.CreateClass("String");
            Symbol = MainScript.CreateClass("Symbol"); Symbol.InstanceMethods.Remove("initialize"); Symbol.Methods.Remove("new");
            Integer = MainScript.CreateClass("Integer");
            Float = MainScript.CreateClass("Float");
            Proc = MainScript.CreateClass("Proc");
            Range = MainScript.CreateClass("Range");
            Array = MainScript.CreateClass("Array");
            Hash = MainScript.CreateClass("Hash");
            Exception = MainScript.CreateClass("Exception");
            StandardError = MainScript.CreateClass("StandardError", InheritsFrom: Exception);
            RuntimeError = MainScript.CreateClass("RuntimeError", InheritsFrom: StandardError);
            Thread = MainScript.CreateClass("Thread");
            Time = MainScript.CreateClass("Time");

            RandomSeed = InternalRandom.NextInt64();
            Random = new Random(RandomSeed.GetHashCode());

            Api.Setup(MainScript);
        }
    }
}
