﻿using System;
using System.Collections.Generic;
using System.Linq;

namespace Embers {
    internal static class Parser {
        public static Expression[] ParseNullSeparatedExpressions(CodeLocation Location, List<RubyObject?> Objects) {
            return ParseExpressions(Location, Objects, Objects => Objects.Split(Object => Object is null, RemoveEmptyEntries: true));
        }
        public static Expression[] ParseCommaSeparatedExpressions(CodeLocation Location, List<RubyObject?> Objects) {
            return ParseExpressions(Location, Objects, Objects => Objects.Split(Object => Object is Token Token && Token.Type is TokenType.Comma));
        }
        static Expression[] ParseExpressions(CodeLocation Location, List<RubyObject?> Objects, Func<List<RubyObject?>, List<List<RubyObject?>>> Split) {
            // Parse general code structure
            ParseGeneralStructure(Objects);
            // Split objects
            List<List<RubyObject?>> SeparatedObjectsList = Split(Objects);
            Expression[] Expressions = new Expression[SeparatedObjectsList.Count];
            // Parse each expression
            for (int i = 0; i < Expressions.Length; i++) {
                Expressions[i] = ParseExpression(Location, SeparatedObjectsList[i]);
            }
            return Expressions;
        }
        static void ParseGeneralStructure(List<RubyObject?> Objects) {
            // Brackets
            ParseBrackets(Objects, TokenType.OpenBracket, TokenType.CloseBracket, (Location, Objects, StartToken)
                => new TempBracketsExpression(Location, Objects, StartToken.WhitespaceBefore));

            // Square brackets
            ParseBrackets(Objects, TokenType.OpenSquareBracket, TokenType.CloseSquareBracket, (Location, Objects, StartToken)
                => new TempSquareBracketsExpression(Location, Objects, StartToken.WhitespaceBefore));

            // Curly brackets
            ParseBrackets(Objects, TokenType.OpenCurlyBracket, TokenType.CloseCurlyBracket, (Location, Objects, StartToken)
                => new TempCurlyBracketsExpression(Location, Objects, StartToken.WhitespaceBefore));

            // Blocks
            ParseBlocks(Objects);
        }
        static void ParseBrackets(List<RubyObject?> Objects, TokenType OpenType, TokenType CloseType, Func<CodeLocation, List<RubyObject?>, Token, Expression> Creator) {
            Stack<(int Index, Token Token)> OpenBrackets = new();
            // Find and condense brackets
            for (int i = 0; i < Objects.Count; i++) {
                RubyObject? Object = Objects[i];

                // Token
                if (Object is Token Token) {
                    // Open bracket
                    if (Token.Type == OpenType) {
                        OpenBrackets.Push((i, Token));
                    }
                    // Close bracket
                    else if (Token.Type == CloseType) {
                        if (OpenBrackets.TryPop(out (int Index, Token Token) OpenBracket)) {
                            // Get bracket objects
                            List<RubyObject?> BracketObjects = Objects.GetIndexRange(OpenBracket.Index + 1, i - 1);
                            Objects.RemoveIndexRange(OpenBracket.Index, i);

                            // Insert expression at open bracket index
                            i = OpenBracket.Index;
                            Objects.Insert(i, Creator(OpenBracket.Token.Location, BracketObjects, OpenBracket.Token));
                        }
                        else {
                            throw new SyntaxError($"{Token.Location}: unexpected '{Token.Value}'");
                        }
                    }
                }
            }
            // Unclosed open bracket
            if (OpenBrackets.TryPop(out (int Index, Token Token) UnclosedOpenBracket)) {
                throw new SyntaxError($"{UnclosedOpenBracket.Token.Location}: unclosed '{UnclosedOpenBracket.Token.Value}'");
            }
        }
        static void ParseBlocks(List<RubyObject?> Objects) {
            Stack<(int Index, Token Token)> StartBlocks = new();
            bool DoBlockValid = true;
            // Find and condense blocks
            for (int i = 0; i < Objects.Count; i++) {
                RubyObject? LastObject = i - 1 >= 0 ? Objects[i - 1] : null;
                RubyObject? Object = Objects[i];

                // Keyword
                if (Object is Token Token && Token.Type is TokenType.Identifier) {
                    // Don't match keywords in path (e.g. 5.class)
                    if (LastObject is Token LastToken && LastToken.Type is TokenType.Dot or TokenType.DoubleColon or TokenType.SafeDot) {
                        continue;
                    }

                    // Start block
                    if ((Token.Value is "begin" or "def" or "for" or "class" or "module" or "case") || (Token.Value is "if" or "unless" or "while" or "until" && LastObject is null) || (Token.Value is "do" && DoBlockValid)) {
                        // Push block to stack
                        StartBlocks.Push((i, Token));
                        // Set do block validity
                        if (Token.Value is "while" or "until") {
                            DoBlockValid = false;
                        }
                    }
                    // End block
                    else if (Token.Value is "end") {
                        if (StartBlocks.TryPop(out (int Index, Token Token) StartBlock)) {
                            // Create locals for block
                            CodeLocation BlockLocation = StartBlock.Token.Location;

                            // Get block objects
                            List<RubyObject?> BlockObjects = Objects.GetIndexRange(StartBlock.Index + 1, i - 1);
                            Objects.RemoveIndexRange(StartBlock.Index, i);

                            // Get block creator
                            Func<CodeLocation, List<RubyObject?>, Expression> Creator = StartBlock.Token.Value switch {
                                "begin" => (Location, Objects) => ParseBeginBlock(Location, BlockObjects),
                                "def" => (Location, Objects) => ParseDefBlock(Location, BlockObjects),
                                "for" => (Location, Objects) => ParseForBlock(Location, BlockObjects),
                                "class" => (Location, Objects) => ParseModuleBlock(Location, BlockObjects, IsClass: true),
                                "module" => (Location, Objects) => ParseModuleBlock(Location, BlockObjects, IsClass: false),
                                "case" => (Location, Objects) => ParseCaseBlock(Location, BlockObjects),
                                "if" => (Location, Objects) => ParseIfBlock(Location, BlockObjects),
                                "unless" => (Location, Objects) => ParseIfBlock(Location, BlockObjects, Negate: true),
                                "while" => (Location, Objects) => ParseWhileBlock(Location, BlockObjects),
                                "until" => (Location, Objects) => ParseWhileBlock(Location, BlockObjects, Negate: true),
                                "do" => (Location, Objects) => ParseDoBlock(Location, BlockObjects),
                                _ => throw new InternalError($"{BlockLocation}: block not handled: '{StartBlock.Token.Value}'")
                            };

                            // Insert block expression at start block index
                            i = StartBlock.Index;
                            Objects.Insert(i, new TempScopeExpression(BlockLocation, BlockObjects, Creator));
                        }
                        else {
                            throw new SyntaxError($"{Token.Location}: unexpected '{Token.Value}'");
                        }
                    }
                }
                // End of statement
                else if (Object is null) {
                    DoBlockValid = true;
                }
            }
            // Unclosed start block
            if (StartBlocks.TryPop(out (int Index, Token Token) UnclosedStartBlock)) {
                throw new SyntaxError($"{UnclosedStartBlock.Token.Location}: unclosed '{UnclosedStartBlock.Token.Value}'");
            }
        }
        static Branch[] ParseBranches(List<RubyObject?> Objects, params string[] BranchOrder) {
            // Find start of branches
            int StartIndex = Objects.FindIndex(Object => Object is Token Token && Token.Type is TokenType.Identifier && BranchOrder.Contains(Token.Value));

            // No branches
            if (StartIndex == -1) {
                return System.Array.Empty<Branch>();
            }

            // Ensure branches are in correct order
            if (BranchOrder.Length >= 2) {
                int Index = 0;
                foreach (RubyObject? Object in Objects) {
                    // Identifier
                    if (Object is Token Token && Token.Type is TokenType.Identifier) {
                        // Check against each branch type
                        for (int i = 0; i < BranchOrder.Length; i++) {
                            // Compare branch types
                            if (BranchOrder[i] == Token.Value) {
                                // Throw error if branch matches passed branch type
                                if (i < Index) {
                                    throw new SyntaxError($"{Token.Location}: invalid {Token.Value} (incorrect order)");
                                }
                                // Move forward
                                Index = i;
                            }
                        }
                    }
                }
            }

            // Parse branches
            List<Branch> Branches = new();
            foreach (string Branch in BranchOrder) {
                while (true) {
                    // Find branch index
                    int BranchIndex = Objects.FindIndex(StartIndex, Object => Object is Token Token && Token.AsIdentifier == Branch);
                    if (BranchIndex == -1) {
                        break;
                    }
                    Token BranchToken = (Token)Objects[BranchIndex]!;

                    // Find branch end index
                    int EndBranchIndex = Objects.FindIndex(BranchIndex + 1,
                        Object => Object is Token Token && Token.Type is TokenType.Identifier && BranchOrder.Contains(Token.Value)
                    );
                    if (EndBranchIndex == -1) {
                        EndBranchIndex = Objects.Count;
                    }

                    // Take branch objects
                    List<RubyObject?> BranchObjects = Objects.GetIndexRange(BranchIndex + 1, EndBranchIndex - 1);
                    Objects.RemoveIndexRange(BranchIndex, EndBranchIndex - 1);
                    
                    // Add branch
                    Branches.Add(new Branch(BranchToken.Location, Branch, BranchObjects));
                }
            }
            return Branches.ToArray();
        }
        sealed class Branch {
            public readonly CodeLocation Location;
            public readonly string Name;
            public readonly List<RubyObject?> Objects;
            public Branch(CodeLocation location, string name, List<RubyObject?> objects) {
                Location = location;
                Name = name;
                Objects = objects;
            }
        }
        static BeginExpression ParseBeginBlock(CodeLocation Location, List<RubyObject?> Objects) {
            // Branches
            Branch[] Branches = ParseBranches(Objects, "rescue", "else", "ensure");

            // Parse begin expressions
            Expression[] BeginExpressions = ParseNullSeparatedExpressions(Location, Objects);

            // Parse branches
            List<RescueExpression> RescueBranches = new();
            Expression[]? ElseExpressions = null;
            Expression[]? EnsureExpressions = null;
            foreach (Branch Branch in Branches) {
                // Rescue
                if (Branch.Name is "rescue") {
                    // Find end of rescue information
                    int NullIndex = Branch.Objects.FindIndex(Object => Object is null);
                    if (NullIndex == -1) {
                        NullIndex = Branch.Objects.Count;
                    }

                    // Find hash rocket index
                    int HashRocketIndex = Branch.Objects.FindIndex(0, NullIndex, Object => Object is Token Token && Token.Type is TokenType.HashRocket);

                    // Get exception type
                    Expression? ExceptionType = null;
                    int EndExceptionType = HashRocketIndex != -1 ? HashRocketIndex - 1 : NullIndex - 1;
                    if (EndExceptionType >= 0) {
                        ExceptionType = ParseExpression(Branch.Location, Branch.Objects.GetIndexRange(0, EndExceptionType));
                    }

                    // Get exception variable
                    string? ExceptionVariable = null;
                    if (HashRocketIndex != -1) {
                        // Exception variable
                        if (HashRocketIndex + 1 < Branch.Objects.Count && Branch.Objects[HashRocketIndex + 1] is Token Variable && Variable.AsIdentifier is string Identifier) {
                            ExceptionVariable = Identifier;
                        }
                        // Error
                        else {
                            throw new SyntaxError($"{Branch.Objects[HashRocketIndex]!.Location}: expected identifier after '=>'");
                        }
                    }

                    // Parse rescue expressions
                    Expression[] RescueExpressions = ParseNullSeparatedExpressions(Branch.Location, Branch.Objects.GetIndexRange(NullIndex + 1));

                    // Create rescue branch
                    RescueBranches.Add(new RescueExpression(Branch.Location, RescueExpressions, ExceptionType, ExceptionVariable));
                }
                // Else
                else if (Branch.Name is "else") {
                    if (ElseExpressions is not null) {
                        throw new SyntaxError($"{Branch.Location}: multiple else branches not valid");
                    }
                    ElseExpressions = ParseNullSeparatedExpressions(Branch.Location, Branch.Objects);
                }
                // Ensure
                else if (Branch.Name is "ensure") {
                    if (EnsureExpressions is not null) {
                        throw new SyntaxError($"{Branch.Location}: multiple ensure branches not valid");
                    }
                    EnsureExpressions = ParseNullSeparatedExpressions(Branch.Location, Branch.Objects);
                }
            }

            // Create begin expression
            return new BeginExpression(Location, BeginExpressions, RescueBranches.ToArray(), ElseExpressions, EnsureExpressions);
        }
        static DefMethodExpression ParseDefBlock(CodeLocation Location, List<RubyObject?> Objects) {
            // Find end of path
            int EndPathIndex;
            bool ExpectIdentifier = true;
            for (EndPathIndex = 0; EndPathIndex < Objects.Count; EndPathIndex++) {
                RubyObject? Object = Objects[EndPathIndex];

                // End of path
                if (!(ExpectIdentifier || Object is Token DotToken && DotToken.Type is TokenType.Dot)) {
                    break;
                }
                // Token
                if (Object is Token Token) {
                    if (Token.Type is TokenType.Dot) {
                        ExpectIdentifier = true;
                    }
                    else if (Token.Type is TokenType.Identifier or TokenType.Operator) {
                        ExpectIdentifier = false;
                    }
                }
            }
            // Get path objects
            List<RubyObject?> PathObjects = Objects.GetIndexRange(0, EndPathIndex - 1);
            // Parse method path
            (ReferenceExpression? PathParent, string PathName) = ParsePath(Location, PathObjects, ConstantPath: false);

            // Parse method= as name
            if (EndPathIndex < Objects.Count && Objects[EndPathIndex] is Token EndPathToken) {
                // '=' follows method name
                if (EndPathToken.Type is TokenType.AssignmentOperator && EndPathToken.Value is "=" && !EndPathToken.WhitespaceBefore) {
                    PathName += "=";
                    EndPathIndex++;
                }
            }

            // Get argument objects
            List<RubyObject?> ArgumentObjects;
            int EndArgumentsIndex;
            // Get argument objects (in brackets)
            if (EndPathIndex < Objects.Count && Objects[EndPathIndex] is TempBracketsExpression ArgumentBrackets) {
                EndArgumentsIndex = EndPathIndex;
                ArgumentObjects = ArgumentBrackets.Objects;
            }
            // Get argument objects (no brackets)
            else {
                EndArgumentsIndex = Objects.FindIndex(EndPathIndex, Object => Object is null);
                if (EndArgumentsIndex == -1) {
                    EndArgumentsIndex = Objects.Count;
                }
                ArgumentObjects = Objects.GetIndexRange(EndPathIndex, EndArgumentsIndex - 1);
            }
            // Parse arguments
            Argument[] Arguments = ParseDefArguments(ArgumentObjects);

            // Parse expressions
            Expression[] Expressions = ParseNullSeparatedExpressions(Location, Objects.GetIndexRange(EndArgumentsIndex + 1));

            // Create define method expression
            return new DefMethodExpression(Location, Expressions, PathParent, PathName, Arguments);
        }
        static ForExpression ParseForBlock(CodeLocation Location, List<RubyObject?> Objects) {
            // Get arguments
            int EndArgumentsIndex = Objects.FindIndex(Object => Object is Token Token && Token.AsIdentifier is "in");
            if (EndArgumentsIndex == -1) {
                throw new SyntaxError($"{Location}: expected 'in' after 'for'");
            }
            // Parse arguments
            Argument[] Arguments = ParseDefArguments(Objects.GetIndexRange(0, EndArgumentsIndex - 1));

            // Get target
            int EndTargetIndex = Objects.FindIndex(EndArgumentsIndex, Object => Object is null || Object is Token Token && Token.AsIdentifier is "do");
            if (EndTargetIndex == -1) {
                EndTargetIndex = Objects.Count;
            }
            // Parse target
            Expression Target = ParseExpression(Location, Objects.GetIndexRange(EndArgumentsIndex + 1, EndTargetIndex - 1));

            // Parse expressions
            Expression[] Expressions = ParseNullSeparatedExpressions(Location, Objects.GetIndexRange(EndTargetIndex + 1));
            // Create block from expressions
            Method BlockMethod = new(Location, Arguments, Expressions);

            // Create define method expression
            return new ForExpression(Location, Expressions, Target, Arguments, BlockMethod);
        }
        static DefModuleExpression ParseModuleBlock(CodeLocation Location, List<RubyObject?> Objects, bool IsClass) {
            // Find end of path
            int EndPathIndex = Objects.FindIndex(Object => Object is null);
            // Get path objects
            List<RubyObject?> PathObjects = Objects.GetIndexRange(0, EndPathIndex - 1);

            // Parse module path and super path
            (ReferenceExpression? Parent, string Name) Path;
            ReferenceExpression? Super;
            int SuperIndex = PathObjects.FindIndex(Object => Object is Token Token && Token.Type is TokenType.Operator && Token.Value is "<");
            // Module < superclass
            if (SuperIndex != -1) {
                // Class
                if (IsClass) {
                    Path = ParsePath(Location, PathObjects.GetIndexRange(0, SuperIndex - 1), ConstantPath: true);
                    Super = ParseCombinedPath(Location, PathObjects.GetIndexRange(SuperIndex + 1), ConstantPath: true);
                }
                // Module
                else {
                    throw new SyntaxError($"{Location}: modules do not support inheritance");
                }
            }
            // Module
            else {
                Path = ParsePath(Location, PathObjects, ConstantPath: true);
                Super = null;
            }

            // Get module expressions
            Expression[] Expressions = ParseNullSeparatedExpressions(Location, Objects.GetIndexRange(EndPathIndex + 1));

            // Create module expression
            return new DefModuleExpression(Location, Expressions, Path.Name, Super, IsClass);
        }
        static CaseExpression ParseCaseBlock(CodeLocation Location, List<RubyObject?> Objects) {
            // Get branches
            Branch[] Branches = ParseBranches(Objects, "when", "else");

            // Parse subject
            Expression Subject = ParseExpression(Location, Objects);

            // Parse branches
            List<WhenExpression> WhenBranches = new();
            Expression[]? ElseBranch = null;
            foreach (Branch Branch in Branches) {
                // When
                if (Branch.Name is "when") {
                    // Get end of condition
                    int EndConditionIndex = Branch.Objects.FindIndex(Object => Object is null);
                    if (EndConditionIndex == -1) {
                        EndConditionIndex = Branch.Objects.Count;
                    }
                    // Get conditions
                    Expression[] Conditions = ParseCommaSeparatedExpressions(Branch.Location, Branch.Objects.GetIndexRange(0, EndConditionIndex - 1));
                    // Parse expressions
                    Expression[] Expressions = ParseNullSeparatedExpressions(Branch.Location, Branch.Objects.GetIndexRange(EndConditionIndex + 1));
                    // Add when expression
                    WhenBranches.Add(new WhenExpression(Branch.Location, Conditions, Expressions));
                }
                // Else
                else if (Branch.Name is "else") {
                    // Ensure else is last branch
                    if (ElseBranch is not null) {
                        throw new SyntaxError($"{Branch.Location}: else must be the last branch");
                    }
                    // Parse expressions
                    Expression[] Expressions = ParseNullSeparatedExpressions(Branch.Location, Branch.Objects);
                    // Add else expression
                    ElseBranch = Expressions;
                }
            }

            // Warn if case expression is empty
            if (WhenBranches.Count == 0) {
                Location.Axis.Warn(Location, "empty case expression");
            }

            // Create case expression
            return new CaseExpression(Location, Subject, WhenBranches, ElseBranch);
        }
        static IfExpression ParseIfBlock(CodeLocation Location, List<RubyObject?> Objects, bool Negate = false) {
            // Branches
            Branch[] Branches = ParseBranches(Objects, "elsif", "else");

            // Parse condition
            Expression ParseCondition(CodeLocation BranchLocation, List<RubyObject?> BranchObjects, out int EndConditionIndex) {
                // Get end of condition
                EndConditionIndex = BranchObjects.FindIndex(Object => Object is null || Object is Token Token && Token.AsIdentifier is "then");
                if (EndConditionIndex == -1) {
                    EndConditionIndex = BranchObjects.Count;
                }
                // Parse condition
                Expression Condition = ParseExpression(BranchLocation, BranchObjects.GetIndexRange(0, EndConditionIndex - 1));
                // Warn if condition is (a = b) not (a == b)
                if (Condition is AssignmentExpression) {
                    Location.Axis.Warn(Location, "assignment found in condition (did you mean to compare?)");
                }
                // Negate condition
                if (Negate) {
                    Condition = new NotExpression(Condition);
                }
                return Condition;
            }

            // Parse condition
            Expression MainCondition = ParseCondition(Location, Objects, out int EndMainConditionIndex);

            // Parse if expressions
            Expression[] MainExpressions = ParseNullSeparatedExpressions(Location, Objects.GetIndexRange(EndMainConditionIndex + 1));

            // Parse branches
            IfExpression? LastBranch = null;
            foreach (Branch Branch in Branches.Reverse()) {
                // Elsif
                if (Branch.Name is "elsif") {
                    // Get end of condition
                    Expression Condition = ParseCondition(Branch.Location, Branch.Objects, out int EndConditionIndex);
                    // Parse elsif expressions
                    Expression[] ElsifExpressions = ParseNullSeparatedExpressions(Branch.Location, Branch.Objects.GetIndexRange(EndConditionIndex + 1));
                    // Add elsif expression
                    LastBranch = LastBranch is not null
                        ? new IfElseExpression(Branch.Location, ElsifExpressions, Condition, LastBranch)
                        : new IfExpression(Branch.Location, ElsifExpressions, Condition);
                }
                // Else
                else if (Branch.Name is "else") {
                    // Ensure else is last branch
                    if (LastBranch is not null) {
                        throw new SyntaxError($"{Branch.Location}: else must be the last branch");
                    }
                    // Parse else expressions
                    Expression[] ElseExpressions = ParseNullSeparatedExpressions(Branch.Location, Branch.Objects);
                    // Add else expression
                    LastBranch = new IfExpression(Branch.Location, ElseExpressions, null);
                }
            }

            // Create if expression
            return LastBranch is not null
                ? new IfElseExpression(Location, MainExpressions, MainCondition, LastBranch)
                : new IfExpression(Location, MainExpressions, MainCondition);
        }
        static WhileExpression ParseWhileBlock(CodeLocation Location, List<RubyObject?> Objects, bool Negate = false) {
            // Get condition
            int EndConditionIndex = Objects.FindIndex(Object => Object is null || Object is Token Token && Token.AsIdentifier is "do");
            Expression Condition = ParseExpression(Location, Objects.GetIndexRange(0, EndConditionIndex - 1));
            // Negate condition if block is 'until'
            if (Negate) {
                Condition = new NotExpression(Condition);
            }
            // Get expressions
            Expression[] Expressions = ParseNullSeparatedExpressions(Location, Objects.GetIndexRange(EndConditionIndex + 1));
            // Create while expression
            return new WhileExpression(Location, Expressions, Condition);
        }
        static TempDoExpression ParseDoBlock(CodeLocation Location, List<RubyObject?> Objects) {
            // Create do block expression
            return new TempDoExpression(Location, Objects);
        }

        static Expression ParseExpression(CodeLocation Location, List<RubyObject?> Objects) {
            // Temporary expressions
            MatchTemporaryExpressions(Objects);

            // Alias
            MatchAlias(Objects);

            // Temp lambda expressions
            MatchTempLambdaExpressions(Objects);

            // Token expressions
            MatchTokenExpressions(Objects);

            // String formatting
            MatchStringFormatting(Objects);

            // Conditional modifiers
            MatchConditionalModifiers(Location, Objects);

            // Lambda expressions (high precedence)
            MatchLambdaExpressions(Objects, HighPrecedence: true);

            // Method calls (brackets)
            MatchMethodCallsBrackets(Objects);

            // Blocks (high precedence)
            MatchBlocks(Objects, HighPrecedence: true);

            // Paths
            MatchPaths(Objects);

            // Indexers
            MatchIndexers(Location, Objects);

            // Unary
            MatchUnary(Objects);

            // Ranges
            MatchRanges(Objects);

            // Defined?
            MatchDefined(Objects);

            // Not (high precedence)
            MatchNot(Objects, HighPrecedence: true);

            // Operators
            MatchOperators(Objects);

            // Logic (high precedence)
            MatchLogic(Objects, HighPrecedence: true);

            // Ternary
            MatchTernary(Objects);

            // Key-value pairs
            MatchKeyValuePairs(Objects);

            // Lambda expressions (low precedence)
            MatchLambdaExpressions(Objects, HighPrecedence: false);

            // Method calls (no brackets)
            MatchMethodCallsNoBrackets(Location, Objects);

            // Blocks (low precedence)
            MatchBlocks(Objects, HighPrecedence: false);

            // Hashes
            MatchHashes(Objects);

            // Control statements
            MatchControlStatements(Location, Objects);

            // Not (low precedence)
            MatchNot(Objects, HighPrecedence: false);

            // Logic (low precedence)
            MatchLogic(Objects, HighPrecedence: false);

            // Assignment
            MatchAssignment(Location, Objects);

            // Extract expression from objects
            Expression? Result = null;
            foreach (RubyObject? Object in Objects) {
                // Expression
                if (Object is Expression Expression && Expression is not TempExpression) {
                    if (Result is not null) {
                        throw new SyntaxError($"{Location}: unexpected expression: '{Expression}'");
                    }
                    Result = Expression;
                }
                // Invalid object
                else if (Object is not null) {
                    throw new SyntaxError($"{Object.Location}: invalid {Object}");
                }
            }
            if (Result is null) {
                throw new SyntaxError($"{Location}: expected expression");
            }
            return Result;
        }

        static (ReferenceExpression? Parent, string Name) ParsePath(CodeLocation Location, List<RubyObject?> Objects, bool ConstantPath) {
            // Get path parts
            List<string> Parts = new();
            CodeLocation LastLocation = Location;
            bool ExpectPart = true;
            foreach (RubyObject? Object in Objects) {
                // Token
                if (Object is Token Token) {
                    LastLocation = Object.Location;

                    // Path separator
                    if (ConstantPath ? Token.Type is TokenType.DoubleColon : Token.Type is TokenType.Dot) {
                        if (ExpectPart) {
                            throw new SyntaxError($"{Token.Location}: unexpected {Token}");
                        }
                        ExpectPart = true;
                    }
                    // Path part
                    else if (Token.Type is TokenType.Identifier) {
                        if (!ExpectPart) {
                            throw new SyntaxError($"{Token.Location}: unexpected '{Token}'");
                        }
                        ExpectPart = false;
                        Parts.Add(Token.Value!);
                    }
                    // Operator as path part
                    else if (Token.Type is TokenType.Operator) {
                        if (ExpectPart) {
                            Parts.Add(Token.Value!);
                        }
                        else {
                            Parts[^1] += Token.Value!;
                        }
                        ExpectPart = false;
                    }
                    // Unexpected token
                    else {
                        throw new SyntaxError($"{Token.Location}: unexpected '{Token}'");
                    }
                }
                // Null
                else if (Object is null) {
                    // Pass
                }
                // Unexpected object
                else {
                    throw new SyntaxError($"{Object.Location}: unexpected '{Object}'");
                }
            }
            // Expected path part
            if (ExpectPart) {
                throw new SyntaxError($"{LastLocation}: expected identifier");
            }

            // Construct path
            ReferenceExpression? Parent = null;
            string Name = Parts.Last();
            foreach (string Part in Parts.SkipLast(1)) {
                // First part
                if (Parent is null) {
                    if (Part == "self") {
                        Parent = new SelfExpression(Location);
                    }
                    else {
                        Parent = new IdentifierExpression(Location, Name);
                    }
                }
                // Sub part
                else {
                    if (ConstantPath) {
                        Parent = new ConstantPathExpression(Parent, Name);
                    }
                    else {
                        Parent = new MethodCallExpression(Location, Parent, Name);
                    }
                }
            }
            return (Parent, Name);
        }
        static ReferenceExpression ParseCombinedPath(CodeLocation Location, List<RubyObject?> Objects, bool ConstantPath) {
            // Parse path parts
            (ReferenceExpression? Parent, string Name) = ParsePath(Location, Objects, ConstantPath);
            // Combine path parts
            return Parent is null
                ? new IdentifierExpression(Location, Name)
                : (ConstantPath
                    ? new ConstantPathExpression(Parent, Name)
                    : new MethodCallExpression(Parent.Location, Parent, Name)
            );
        }
        static Argument[] ParseDefArguments(List<RubyObject?> Objects) {
            List<Argument> Arguments = new();

            ArgumentType CurrentArgumentType = ArgumentType.Normal;
            bool ExpectArgument = true;
            for (int i = 0; i < Objects.Count; i++) {
                RubyObject? Object = Objects[i];
                RubyObject? NextObject = i + 1 < Objects.Count ? Objects[i + 1] : null;

                // Token
                if (Object is Token Token) {
                    // Identifier
                    if (Token.Type is TokenType.Identifier) {
                        // Unexpected argument
                        if (!ExpectArgument) {
                            throw new SyntaxError($"{Token.Location}: unexpected argument: '{Token}'");
                        }
                        // Default value
                        Expression? DefaultValue = null;
                        if (NextObject is Token NextToken && NextToken.Type is TokenType.AssignmentOperator && NextToken.Value is "=") {
                            // Find end of default value objects
                            int StartDefaultValueIndex = i + 2;
                            for (i = StartDefaultValueIndex; i < Objects.Count; i++) {
                                if (Objects[i] is Token Token2 && Token2.Type is TokenType.Comma) {
                                    break;
                                }
                            }
                            // Get default value objects
                            List<RubyObject?> DefaultValueObjects = Objects.GetIndexRange(StartDefaultValueIndex, i - 1);
                            // Parse default value
                            DefaultValue = ParseExpression(Token.Location, DefaultValueObjects);
                        }
                        // Add argument
                        Arguments.Add(new Argument(Token.Location, Token.Value!, DefaultValue, CurrentArgumentType));
                        // Reset flags
                        ExpectArgument = false;
                        CurrentArgumentType = ArgumentType.Normal;
                    }
                    // Comma
                    else if (Token.Type is TokenType.Comma) {
                        // Unexpected comma
                        if (ExpectArgument) {
                            throw new SyntaxError($"{Token.Location}: expected argument before comma");
                        }
                        // Expect an argument after comma
                        ExpectArgument = true;
                    }
                    // Splat / Double Splat / Block
                    else if (Token.Type is TokenType.Operator && Token.Value is "*" or "**" or "&") {
                        // Unexpected splat or block
                        if (CurrentArgumentType is not ArgumentType.Normal) {
                            throw new SyntaxError($"{Token.Location}: unexpected '{Token}'");
                        }
                        // Modify next argument
                        CurrentArgumentType = Token.Value switch {
                            "*" => ArgumentType.Splat,
                            "**" => ArgumentType.DoubleSplat,
                            "&" or _ => ArgumentType.Block,
                        };
                    }
                    // Invalid
                    else {
                        throw new SyntaxError($"{Token.Location}: unexpected '{Token}'");
                    }
                }
                // Pass
                else if (Object is null) { }
                // Invalid
                else {
                    throw new SyntaxError($"{Object.Location}: unexpected '{Object}'");
                }
            }
            // Unexpected comma
            if (ExpectArgument && Arguments.Count != 0) {
                throw new SyntaxError($"{Arguments[^1].Location}: expected argument before comma");
            }

            return Arguments.ToArray();
        }
        static Argument[] ParseBlockArguments(List<RubyObject?> Objects) {
            // Parse block arguments
            Argument[] Arguments = System.Array.Empty<Argument>();
            // Start block arguments
            if (Objects.FirstOrDefault() is Token FirstToken && FirstToken.Type is TokenType.Operator && FirstToken.Value is "|") {
                // Find end of arguments
                int EndArgumentsIndex = Objects.FindIndex(1, Object => Object is Token NextToken && NextToken.Type is TokenType.Operator && NextToken.Value is "|");
                if (EndArgumentsIndex == -1) {
                    throw new SyntaxError($"{FirstToken.Location}: unclosed '|'");
                }
                // Parse arguments
                Arguments = ParseDefArguments(Objects.GetIndexRange(1, EndArgumentsIndex - 1));
                // Remove arguments
                Objects.RemoveIndexRange(0, EndArgumentsIndex);
            }
            return Arguments;
        }
        static Expression[] ParseCallArgumentsNoBrackets(CodeLocation Location, List<RubyObject?> Objects, int StartIndex) {
            List<Expression> Arguments = new();
            List<RubyObject?> CurrentArgument = new();

            void SubmitArgument() {
                if (CurrentArgument.Count != 0) {
                    Arguments.Add(ParseExpression(Location, CurrentArgument));
                    CurrentArgument.Clear();
                }
            }

            bool ExpectArgument = true;
            int Index = StartIndex;
            for (; Index < Objects.Count; Index++) {
                RubyObject? Object = Objects[Index];

                // Token
                if (Object is Token Token) {
                    // Comma
                    if (Token.Type is TokenType.Comma) {
                        // Unexpected comma
                        if (ExpectArgument) {
                            throw new SyntaxError($"{Token.Location}: expected argument before comma");
                        }
                        // Submit argument
                        SubmitArgument();
                        // Expect an argument after comma
                        ExpectArgument = true;
                    }
                    // End of arguments
                    else {
                        break;
                    }
                }
                // Argument
                else if (Object is Expression Expression) {
                    // Block - end of arguments
                    if (Expression is TempDoExpression or TempCurlyBracketsExpression) {
                        break;
                    }
                    // Reset flag
                    if (ExpectArgument) {
                        ExpectArgument = false;
                    }
                    // Add argument object
                    CurrentArgument.Add(Expression);
                }
                // End of statement
                else if (Object is null) {
                    // Ignore end of statement
                    if (ExpectArgument) {
                        // Pass
                    }
                    // End of arguments
                    else {
                        break;
                    }
                }
                // Invalid
                else {
                    throw new SyntaxError($"{Object.Location}: unexpected '{Object}'");
                }
            }
            // Unexpected comma
            if (ExpectArgument && Arguments.Count != 0) {
                throw new SyntaxError($"{Arguments[^1].Location}: expected argument before comma");
            }
            // Submit argument
            SubmitArgument();

            // Remove arguments
            Objects.RemoveIndexRange(StartIndex, Index - 1);

            return Arguments.ToArray();
        }
        
        static void MatchTemporaryExpressions(List<RubyObject?> Objects) {
            for (int i = 0; i < Objects.Count; i++) {
                RubyObject? LastObject = i - 1 >= 0 ? Objects[i - 1] : null;
                RubyObject? Object = Objects[i];

                // Scope expression
                if (Object is TempScopeExpression ScopeExpression) {
                    // Create scope expression
                    Objects[i] = ScopeExpression.Create(Object.Location);
                }
                // Brackets expression
                else if (Object is TempBracketsExpression BracketsExpression) {
                    // Method call brackets
                    if (LastObject is Token LastToken && LastToken.Type is TokenType.Identifier && !LastToken.WhitespaceAfter && !LastToken.IsKeyword) {
                        // Parse comma-separated expressions
                        Expression[] Expressions = ParseCommaSeparatedExpressions(Object.Location, BracketsExpression.Objects);
                        BracketsExpression.Expressions = Expressions;
                    }
                    // Single brackets
                    else {
                        // Expand brackets expressions
                        Objects[i] = ParseExpression(Object.Location, BracketsExpression.Objects);
                    }
                }
                // Square brackets expression
                else if (Object is TempSquareBracketsExpression SquareBracketsExpression) {
                    // Parse comma-separated expressions
                    Expression[] Expressions = ParseCommaSeparatedExpressions(Object.Location, SquareBracketsExpression.Objects);
                    // Create array expression
                    Objects[i] = new ArrayExpression(Object.Location, Expressions, SquareBracketsExpression.WhitespaceBefore);
                }
            }
        }
        static void MatchAlias(List<RubyObject?> Objects) {
            for (int i = 0; i < Objects.Count; i++) {
                RubyObject? Object = Objects[i];
                RubyObject? NextObject = i + 1 < Objects.Count ? Objects[i + 1] : null;
                RubyObject? NextNextObject = i + 2 < Objects.Count ? Objects[i + 2] : null;

                // Alias keyword
                if (Object is Token Token && Token.Value is "alias") {
                    // Alias
                    if (NextObject is Token Alias && Alias.Type is TokenType.Identifier) {
                        // Original
                        if (NextNextObject is Token Original && Original.Type is TokenType.Identifier) {
                            // Remove alias objects
                            Objects.RemoveRange(i, 3);
                            // Insert alias expression
                            Objects.Insert(i, new AliasExpression(Token.Location, Alias.Value!, Original.Value!));
                        }
                        else {
                            throw new SyntaxError($"{Token.Location}: expected method identifier for original after 'alias'");
                        }
                    }
                    else {
                        throw new SyntaxError($"{Token.Location}: expected method identifier for alias after 'alias'");
                    }
                }
            }
        }
        static void MatchTempLambdaExpressions(List<RubyObject?> Objects) {
            for (int i = 0; i < Objects.Count; i++) {
                RubyObject? Object = Objects[i];

                // Lambda
                if (Object is Token Token && Token.Type is TokenType.Lambda) {
                    // Find block
                    int IndexOfBlock = Objects.FindIndex(i + 1, Object => Object is TempCurlyBracketsExpression or TempDoExpression);
                    if (IndexOfBlock == -1) {
                        throw new SyntaxError($"{Token.Location}: expected block after '->'");
                    }
                    // Parse arguments
                    Argument[] Arguments = ParseDefArguments(Objects.GetIndexRange(i + 1, IndexOfBlock - 1));
                    // Remove lambda objects
                    Objects.RemoveIndexRange(i, IndexOfBlock - 1);
                    // Insert temp lambda expression
                    Objects.Insert(i, new TempLambdaExpression(Token.Location, Arguments));
                }
            }
        }
        static void MatchTokenExpressions(List<RubyObject?> Objects) {
            for (int i = 0; i < Objects.Count; i++) {
                RubyObject? LastObject = i - 1 >= 0 ? Objects[i - 1] : null;
                RubyObject? Object = Objects[i];

                // Token
                if (Object is Token Token) {
                    // Literal
                    if (Token.IsTokenLiteral) {
                        Objects[i] = new TokenLiteralExpression(Token);
                    }
                    // Identifier
                    else if (Token.Type is TokenType.Identifier) {
                        // Identifier if follows dot
                        if (LastObject is Token LastToken && LastToken.Type is TokenType.Dot or TokenType.SafeDot or TokenType.DoubleColon) {
                            Objects[i] = new IdentifierExpression(Token.Location, Token.Value!);
                        }
                        // self
                        else if (Token.Value is "self") {
                            Objects[i] = new SelfExpression(Token.Location);
                        }
                        // __LINE__
                        else if (Token.Value is "__LINE__") {
                            Objects[i] = new LineExpression(Token.Location);
                        }
                        // __FILE__
                        else if (Token.Value is "__FILE__") {
                            Objects[i] = new FileExpression(Token.Location);
                        }
                        // block_given?
                        else if (Token.Value is "block_given?") {
                            Objects[i] = new BlockGivenExpression(Token.Location);
                        }
                        // Keyword
                        else if (Token.IsKeyword) {
                            // Pass
                        }
                        // Identifier
                        else {
                            Objects[i] = new IdentifierExpression(Token.Location, Token.Value!);
                        }
                    }
                    // Global variable
                    else if (Token.Type is TokenType.GlobalVariable) {
                        Objects[i] = new GlobalExpression(Token.Location, Token.Value!);
                    }
                    // Class variable
                    else if (Token.Type is TokenType.ClassVariable) {
                        Objects[i] = new ClassVariableExpression(Token.Location, Token.Value!);
                    }
                    // Instance variable
                    else if (Token.Type is TokenType.InstanceVariable) {
                        Objects[i] = new InstanceVariableExpression(Token.Location, Token.Value!);
                    }
                }
            }
        }
        static void MatchStringFormatting(List<RubyObject?> Objects) {
            for (int i = 0; i < Objects.Count; i++) {
                RubyObject? Object = Objects[i];

                // Literal
                if (Object is TokenLiteralExpression TokenLiteralExpression) {
                    Token Token = TokenLiteralExpression.Token;

                    // Formatted string
                    if (Token.Type is TokenType.String && Token.Formatted) {
                        string String = Token.Value!;

                        // Get format components
                        List<object> Components = new();
                        int Index = 0;
                        while (Index < String.Length) {
                            // Find '#{'
                            int StartFormattingIndex = String.IndexOf("#{", Index);
                            // Otherwise add rest of characters as literal
                            if (StartFormattingIndex == -1) {
                                Components.Add(String[Index..]);
                                break;
                            }
                            // Find '}'
                            int EndFormattingIndex = String.IndexOf("}", StartFormattingIndex + "#{".Length);
                            // Otherwise error
                            if (EndFormattingIndex == -1) {
                                throw new SyntaxError($"{Token.Location}: expected '}}' to conclude '#{{'");
                            }
                            // Get literal up to '#{'
                            string Literal = String[Index..StartFormattingIndex];
                            // Add literal if it's not empty
                            if (Literal.Length != 0) {
                                Components.Add(Literal);
                            }
                            // Get expression in '#{}'
                            string Expression = String[(StartFormattingIndex + "#{".Length)..EndFormattingIndex];
                            // Add parsed expression
                            Components.Add(ParseExpression(Token.Location, Lexer.Analyse(Token.Location, Expression).CastTo<RubyObject?>()));
                            // Move on
                            Index = EndFormattingIndex + 1;
                        }
                        // Create formatted string expression if string contains any formatting
                        if (Components.Find(Component => Component is not string) is not null) {
                            Objects[i] = new FormattedStringExpression(Token.Location, String, Components.ToArray());
                        }
                    }
                }
            }
        }
        static void MatchConditionalModifiers(CodeLocation Location, List<RubyObject?> Objects) {
            for (int i = 0; i < Objects.Count; i++) {
                RubyObject? Object = Objects[i];

                // Conditional modifier
                if (Object is Token Token && Token.AsIdentifier is "if" or "unless" or "while" or "until" or "rescue") {
                    // Get statement and condition
                    Expression Statement = ParseExpression(Location, Objects.GetIndexRange(0, i - 1));
                    Expression Condition = ParseExpression(Location, Objects.GetIndexRange(i + 1));
                    // Remove objects
                    Objects.Clear();
                    // Create conditional expression
                    Expression ConditionalExpression = Token.Value switch {
                        "if" => new IfModifierExpression(Condition, Statement),
                        "unless" => new IfModifierExpression(new NotExpression(Condition), Statement),
                        "while" => new WhileModifierExpression(Condition, Statement),
                        "until" => new WhileModifierExpression(new NotExpression(Condition), Statement),
                        "rescue" => new RescueModifierExpression(Statement, Condition),
                        _ => throw new InternalError($"{Token.Location}: '{Token.Value}' modifier not handled")
                    };
                    // Insert conditional expression
                    Objects.Insert(0, ConditionalExpression);
                }
            }
        }
        static void MatchMethodCallsBrackets(List<RubyObject?> Objects) {
            for (int i = 0; i < Objects.Count; i++) {
                RubyObject? Object = Objects[i];
                RubyObject? NextObject = i + 1 < Objects.Count ? Objects[i + 1] : null;

                // Method call
                if (Object is ReferenceExpression MethodPath) {
                    // Get arguments in brackets
                    if (NextObject is TempBracketsExpression BracketsExpression && !BracketsExpression.WhitespaceBefore) {
                        // Remove method path and brackets arguments
                        Objects.RemoveRange(i, 2);
                        // Take arguments
                        Expression[] Arguments = BracketsExpression.Expressions!;

                        // Identifier + (arguments)
                        if (MethodPath is IdentifierExpression Identifier) {
                            // Insert method call with arguments
                            Objects.Insert(i, new MethodCallExpression(Identifier.Location, null, Identifier.Name, Arguments, arguments_final: true));
                        }
                        // Method call + (arguments)
                        else {
                            MethodCallExpression MethodCall = (MethodCallExpression)MethodPath;
                            if (MethodCall.Arguments is not null) {
                                throw new SyntaxError($"{BracketsExpression.Location}: unexpected arguments");
                            }
                            // Insert method call with arguments
                            Objects.Insert(i, new MethodCallExpression(MethodCall.Location, MethodCall.Parent, MethodCall.Name, MethodCall.Arguments, MethodCall.Block, arguments_final: true));
                        }
                    }
                }
            }
        }
        static void MatchLambdaExpressions(List<RubyObject?> Objects, bool HighPrecedence) {
            for (int i = 0; i < Objects.Count; i++) {
                RubyObject? Object = Objects[i];
                RubyObject? NextObject = i + 1 < Objects.Count ? Objects[i + 1] : null;

                // Lambda
                if (Object is TempLambdaExpression TempLambdaExpression) {
                    // Block
                    if (HighPrecedence ? NextObject is TempCurlyBracketsExpression : NextObject is TempDoExpression) {
                        // Remove lambda and block
                        Objects.RemoveRange(i, 2);
                        // Parse expressions
                        Expression[] Expressions = ParseNullSeparatedExpressions(NextObject.Location, ((TempExpression)NextObject).Objects);
                        // Insert lambda expression
                        Objects.Insert(i, new LambdaExpression(TempLambdaExpression.Location, TempLambdaExpression.Arguments, Expressions));
                    }
                }
            }
        }
        static void MatchBlocks(List<RubyObject?> Objects, bool HighPrecedence) {
            for (int i = 0; i < Objects.Count; i++) {
                RubyObject? Object = Objects[i];
                RubyObject? NextObject = i + 1 < Objects.Count ? Objects[i + 1] : null;
                
                // Method reference
                if (Object is IdentifierExpression or MethodCallExpression) {
                    // Block expression
                    if (HighPrecedence ? NextObject is TempCurlyBracketsExpression : NextObject is TempDoExpression) {
                        // Get block
                        TempExpression Block = (TempExpression)NextObject;
                        // Remove method reference and block
                        Objects.RemoveRange(i, 2);

                        // Parse block method
                        Method BlockMethod = new(Block.Location, ParseBlockArguments(Block.Objects), ParseNullSeparatedExpressions(Block.Location, Block.Objects));

                        // Identifier + block
                        if (Object is IdentifierExpression LastIdentifier) {
                            // Insert method call with block
                            Objects.Insert(i, new MethodCallExpression(LastIdentifier.Location, null, LastIdentifier.Name, block: BlockMethod, arguments_final: true));
                        }
                        // Method call + block
                        else {
                            // Get method call
                            MethodCallExpression LastMethodCall = (MethodCallExpression)Object;
                            // Ensure block is not already present
                            if (LastMethodCall.Block is not null) {
                                throw new SyntaxError($"{Block.Location}: unexpected block");
                            }
                            // Insert method call with block
                            Objects.Insert(i, new MethodCallExpression(LastMethodCall.Location, LastMethodCall.Parent, LastMethodCall.Name, LastMethodCall.Arguments, BlockMethod, arguments_final: true));
                        }
                    }
                }
            }
        }
        static void MatchPaths(List<RubyObject?> Objects) {
            for (int i = 0; i < Objects.Count; i++) {
                RubyObject? Object = Objects[i];
                RubyObject? NextObject = i + 1 < Objects.Count ? Objects[i + 1] : null;
                RubyObject? NextNextObject = i + 2 < Objects.Count ? Objects[i + 2] : null;

                // Parent
                if (Object is Expression Parent) {
                    // Token
                    if (NextObject is Token NextToken) {
                        // Dot ('.' or '&.')
                        if (NextToken.Type is TokenType.Dot or TokenType.SafeDot) {
                            // Remove path objects
                            Objects.RemoveRange(i, 3);
                            // Child identifier (a.b)
                            if (NextNextObject is IdentifierExpression ChildIdentifier) {
                                // Insert method call expression
                                Objects.Insert(i, new MethodCallExpression(
                                    Parent.Location, Parent, ChildIdentifier.Name, safe_navigation: NextToken.Type is TokenType.SafeDot
                                ));
                            }
                            // Child method call (a.b())
                            else if (NextNextObject is MethodCallExpression ChildMethodCall) {
                                // Insert method call expression
                                Objects.Insert(i, new MethodCallExpression(
                                    Parent.Location, Parent, ChildMethodCall.Name, ChildMethodCall.Arguments, ChildMethodCall.Block, safe_navigation: NextToken.Type is TokenType.SafeDot, arguments_final: ChildMethodCall.ArgumentsFinal
                                ));
                            }
                            // Invalid path
                            else {
                                throw new SyntaxError($"{NextObject.Location}: expected identifier after '.', got '{NextNextObject}'");
                            }
                            // Reprocess path (a.b.c)
                            i--;
                        }
                        // Double colon ('::')
                        else if (NextToken.Type is TokenType.DoubleColon) {
                            // Child
                            if (NextNextObject is IdentifierExpression Child) {
                                // Remove path objects
                                Objects.RemoveRange(i, 3);
                                // Get constant parent
                                ReferenceExpression ConstantParent = Parent as ReferenceExpression
                                    ?? throw new SyntaxError($"{Parent.Location}: constant path must have constant parent");
                                // Insert constant path expression
                                Objects.Insert(i, new ConstantPathExpression(ConstantParent, Child.Name));
                                // Reprocess path (A::B::C)
                                i--;
                            }
                            // Invalid path
                            else {
                                throw new SyntaxError($"{NextObject.Location}: expected identifier after '::', got '{NextNextObject}'");
                            }
                        }
                    }
                }
            }
        }
        static void MatchIndexers(CodeLocation Location, List<RubyObject?> Objects) {
            for (int i = 0; i < Objects.Count; i++) {
                RubyObject? Object = Objects[i];
                RubyObject? NextObject = i + 1 < Objects.Count ? Objects[i + 1] : null;
                RubyObject? NextNextObject = i + 2 < Objects.Count ? Objects[i + 2] : null;

                // Indexer path
                if (Object is Expression Path) {
                    // Indexer
                    if (NextObject is ArrayExpression Indexer && !Indexer.WhitespaceBefore) {
                        // Set index
                        if (NextNextObject is Token NextNextToken && NextNextToken.Type is TokenType.AssignmentOperator) {
                            // Get value objects
                            List<RubyObject?> ValueObjects = Objects.GetIndexRange(i + 3);
                            if (ValueObjects.Count == 0) throw new SyntaxError($"{NextNextToken.Location}: expected value after '{NextNextToken}'");
                            // Get value
                            Expression Value = ParseExpression(Location, ValueObjects);
                            // Remove set index objects
                            Objects.RemoveIndexRange(i);
                            // Get index assignment arguments
                            Expression[] SetIndexArguments = Indexer.Items.Append(Value).ToArray();
                            // Insert index assignment call
                            Objects.Insert(i, new MethodCallExpression(Path.Location, Path, "[]=", SetIndexArguments));
                        }
                        // Index
                        else {
                            // Remove indexer objects
                            Objects.RemoveRange(i, 2);
                            // Insert indexer call
                            Objects.Insert(i, new MethodCallExpression(Path.Location, Path, "[]", Indexer.Items));
                        }
                        // Reprocess indexer (a[b][c])
                        i--;
                    }
                }
            }
        }
        static void MatchUnary(List<RubyObject?> Objects) {
            for (int i = Objects.Count - 1; i >= 0; i--) {
                RubyObject? LastObject = i - 1 >= 0 ? Objects[i - 1] : null;
                RubyObject? Object = Objects[i];
                RubyObject? NextObject = i + 1 < Objects.Count ? Objects[i + 1] : null;

                // Plus or Minus
                if (Object is Token Token && Token.Type is TokenType.Operator && Token.Value is "+" or "-") {
                    // Expression
                    if (NextObject is Expression NextExpression) {
                        // Resolve arithmetic / unary ambiguity
                        if (LastObject is not Expression || (Token.WhitespaceBefore && !Token.WhitespaceAfter)) {
                            // Remove unary operator and expression
                            Objects.RemoveRange(i, 2);
                            // Insert unary method call expression
                            Objects.Insert(i, new MethodCallExpression(Token.Location, NextExpression, $"{Token.Value}@"));
                        }
                    }
                }
            }
        }
        static void MatchRanges(List<RubyObject?> Objects) {
            for (int i = Objects.Count - 1; i >= 0; i--) {
                RubyObject? LastObject = i - 1 >= 0 ? Objects[i - 1] : null;
                RubyObject? Object = Objects[i];
                RubyObject? NextObject = i + 1 < Objects.Count ? Objects[i + 1] : null;

                // Range
                if (Object is Token Token && Token.Type is TokenType.InclusiveRange or TokenType.ExclusiveRange) {
                    // Get min and max
                    Expression? Min = LastObject is Expression LastExp ? LastExp : null;
                    Expression? Max = NextObject is Expression NextExp ? NextExp : null;

                    // Remove objects
                    if (Max is not null) {
                        Objects.RemoveAt(i + 1);
                    }
                    Objects.RemoveAt(i);
                    if (Min is not null) {
                        Objects.RemoveAt(i - 1);
                        i--;
                    }

                    // Insert range expression
                    Objects.Insert(i, new RangeExpression(Token.Location, Min, Max, Token.Type is TokenType.ExclusiveRange));
                }
            }
        }
        static void MatchDefined(List<RubyObject?> Objects) {
            for (int i = 0; i < Objects.Count; i++) {
                RubyObject? Object = Objects[i];
                RubyObject? NextObject = i + 1 < Objects.Count ? Objects[i + 1] : null;

                // Defined?
                if (Object is Token Token && Token.AsIdentifier is "defined?") {
                    // Expression
                    if (NextObject is Expression NextExpression) {
                        // Remove defined? and expression
                        Objects.RemoveRange(i, 2);
                        // Insert defined? expression
                        Objects.Insert(i, new DefinedExpression(Token.Location, NextExpression));
                    }
                    // Error
                    else {
                        throw new SyntaxError($"{Token.Location}: expected expression after defined?");
                    }
                }
            }
        }
        static void MatchNot(List<RubyObject?> Objects, bool HighPrecedence) {
            for (int i = Objects.Count - 1; i >= 0; i--) {
                RubyObject? Object = Objects[i];
                RubyObject? NextObject = i + 1 < Objects.Count ? Objects[i + 1] : null;

                // Not
                if (Object is Token Token && Token.Type is TokenType.Not) {
                    // Ignore until later if low precedence
                    if (HighPrecedence && Token.Value is not "!") {
                        continue;
                    }
                    // Expression to negate
                    if (NextObject is Expression NextExpression) {
                        // Remove objects
                        Objects.RemoveRange(i, 2);
                        // Insert not expression
                        Objects.Insert(i, new NotExpression(NextExpression));
                    }
                }
            }
        }
        static void MatchOperators(List<RubyObject?> Objects) {
            void Match(Func<string, bool> Filter) {
                for (int i = 0; i < Objects.Count; i++) {
                    RubyObject? Object = Objects[i];
                    RubyObject? NextObject = i + 1 < Objects.Count ? Objects[i + 1] : null;
                    RubyObject? NextNextObject = i + 2 < Objects.Count ? Objects[i + 2] : null;

                    // Left expression
                    if (Object is Expression LeftExpression) {
                        // Operator
                        if (NextObject is Token NextToken && NextToken.Type is TokenType.Operator && Filter(NextToken.Value!)) {
                            // Right expression
                            if (NextNextObject is Expression RightExpression) {
                                // Remove left expression, operator, and right expression
                                Objects.RemoveRange(i, 3);
                                // Insert method call expression
                                Objects.Insert(i, new MethodCallExpression(LeftExpression.Location, LeftExpression, NextToken.Value!, new Expression[] { RightExpression }));
                                // Reprocess operator (a + b + c)
                                i--;
                            }
                            // Right expression missing
                            else {
                                throw new SyntaxError($"{NextToken.Location}: expected expression after '{NextToken}'");
                            }
                        }
                    }
                }
            }
            // Operator precedence
            Match(Op => Op is "**");
            Match(Op => Op is "*" or "/" or "%");
            Match(Op => Op is "+" or "-");
            Match(Op => Op is "<<" or ">>");
            Match(Op => Op is "&");
            Match(Op => Op is "|");
            Match(Op => Op is "<" or "<=" or ">=" or ">");
            Match(Op => Op is "==" or "===" or "!=" or "<=>");
        }
        static void MatchLogic(List<RubyObject?> Objects, bool HighPrecedence) {
            void Match(Func<string, bool> Filter, bool IsAnd) {
                for (int i = 0; i < Objects.Count; i++) {
                    RubyObject? Object = Objects[i];
                    RubyObject? NextObject = i + 1 < Objects.Count ? Objects[i + 1] : null;
                    RubyObject? NextNextObject = i + 2 < Objects.Count ? Objects[i + 2] : null;

                    // Left expression
                    if (Object is Expression LeftExpression) {
                        // Logic operator
                        if (NextObject is Token NextToken && NextToken.Type is TokenType.LogicOperator && Filter(NextToken.Value!)) {
                            // Right expression
                            if (NextNextObject is Expression RightExpression) {
                                // Remove left expression, logic operator, and right expression
                                Objects.RemoveRange(i, 3);
                                // Insert logic expression
                                Objects.Insert(i, new LogicExpression(LeftExpression, RightExpression, IsAnd));
                                // Reprocess logic (a and b and c)
                                i--;
                            }
                            // Right expression missing
                            else {
                                throw new SyntaxError($"{NextToken.Location}: expected expression after '{NextToken}'");
                            }
                        }
                    }
                }
            }
            // Operator precedence
            if (HighPrecedence) {
                Match(Op => Op is "&&", IsAnd: true);
                Match(Op => Op is "||", IsAnd: false);
            }
            else {
                Match(Op => Op is "and", IsAnd: true);
                Match(Op => Op is "or", IsAnd: false);
            }
        }
        static void MatchTernary(List<RubyObject?> Objects) {
            for (int i = 0; i < Objects.Count; i++) {
                RubyObject? Object = Objects[i];
                RubyObject? NextObject = i + 1 < Objects.Count ? Objects[i + 1] : null;

                // Condition
                if (Object is Expression Condition) {
                    // '?'
                    if (NextObject is Token TernaryTruthyToken && TernaryTruthyToken.Type is TokenType.TernaryTruthy) {
                        // Find '?' and ':'
                        int TernaryTruthyIndex = i + 1;
                        int TernaryFalseyIndex = Objects.FindIndex(Object => Object is Token Token && Token.Type is TokenType.TernaryFalsey);
                        Token TernaryFalseyToken = TernaryFalseyIndex != -1
                            ? (Token)Objects[TernaryFalseyIndex]!
                            : throw new SyntaxError($"{TernaryTruthyToken.Location}: incomplete ternary (expected ':' after '?')");

                        // Get expression after '?'
                        Expression ExpressionIfTruthy = ParseExpression(TernaryTruthyToken.Location, Objects.GetIndexRange(TernaryTruthyIndex + 1, TernaryFalseyIndex - 1));

                        // Get expression after ':'
                        Expression ExpressionIfFalsey = (TernaryFalseyIndex + 1 < Objects.Count ? Objects[TernaryFalseyIndex + 1] as Expression : null)
                            ?? throw new SyntaxError($"{TernaryFalseyToken.Location}: expected expression after ':'");

                        // Remove ternary objects
                        Objects.RemoveIndexRange(i, TernaryFalseyIndex + 1);
                        // Insert ternary expression
                        Objects.Insert(i, new TernaryExpression(Condition, ExpressionIfTruthy, ExpressionIfFalsey));
                    }
                }
            }
        }
        static void MatchKeyValuePairs(List<RubyObject?> Objects) {
            for (int i = 0; i < Objects.Count; i++) {
                RubyObject? Object = Objects[i];
                RubyObject? NextObject = i + 1 < Objects.Count ? Objects[i + 1] : null;
                RubyObject? NextNextObject = i + 2 < Objects.Count ? Objects[i + 2] : null;

                // Key expression
                if (Object is Expression KeyExpression) {
                    // Hash rocket
                    if (NextObject is Token NextToken && NextToken.Type is TokenType.HashRocket) {
                        // Value expression
                        if (NextNextObject is Expression ValueExpression) {
                            // Remove key-value pair objects
                            Objects.RemoveRange(i, 3);
                            // Insert key-value pair expression
                            Objects.Insert(i, new KeyValuePairExpression(KeyExpression, ValueExpression));
                        }
                        // Invalid
                        else {
                            throw new SyntaxError($"{(NextNextObject ?? NextToken).Location}: expected value after '=>', got '{NextNextObject}'");
                        }
                    }
                }
            }
        }
        static void MatchMethodCallsNoBrackets(CodeLocation Location, List<RubyObject?> Objects) {
            for (int i = Objects.Count - 1; i >= 0; i--) {
                RubyObject? Object = Objects[i];
                RubyObject? NextObject = i + 1 < Objects.Count ? Objects[i + 1] : null;

                // Method call
                if (Object is ReferenceExpression Reference && Reference is IdentifierExpression or MethodCallExpression) {
                    // No arguments
                    if (NextObject is (not Expression) or TempDoExpression or TempCurlyBracketsExpression) {
                        continue;
                    }
                    // Arguments final or already present
                    if (Reference is MethodCallExpression MethodCallReference && (MethodCallReference.ArgumentsFinal || MethodCallReference.Arguments.Length != 0)) {
                        continue;
                    }

                    // Take call arguments
                    Expression[] Arguments = ParseCallArgumentsNoBrackets(Location, Objects, i + 1);
                    // Get parent
                    Expression? Parent = null;
                    if (Object is MethodCallExpression MethodCall) {
                        Parent = MethodCall.Parent;
                    }
                    // Create method call expression
                    Objects[i] = new MethodCallExpression(Object.Location, Parent, Reference.Name, Arguments);
                }
            }
        }
        static void MatchHashes(List<RubyObject?> Objects) {
            for (int i = 0; i < Objects.Count; i++) {
                RubyObject? Object = Objects[i];

                // Curly brackets expression
                if (Object is TempCurlyBracketsExpression CurlyBracketsExpression) {
                    // Get expressions in hash
                    Expression[] Expressions = ParseCommaSeparatedExpressions(CurlyBracketsExpression.Location, CurlyBracketsExpression.Objects);
                    // Build key-value dictionary
                    Dictionary<Expression, Expression> HashExpressions = new(Expressions.Length);
                    foreach (Expression Item in Expressions) {
                        // Key-value pair
                        if (Item is KeyValuePairExpression KeyValuePair) {
                            HashExpressions.Add(KeyValuePair.Key, KeyValuePair.Value);
                        }
                        // Invalid
                        else {
                            throw new SyntaxError($"{Item.Location}: expected key-value pair, got '{Item}'");
                        }
                    }
                    // Create hash expression
                    Objects[i] = new HashExpression(CurlyBracketsExpression.Location, HashExpressions);
                }
            }
        }
        static void MatchControlStatements(CodeLocation Location, List<RubyObject?> Objects) {
            bool GetControlStatement(string Keyword, CodeLocation Location, out bool TakeArguments, out Func<Expression[], Expression>? Creator) {
                // Return whether the keyword is a control statement, whether it takes arguments, and a control expression creator
                switch (Keyword) {
                    case "break":
                        TakeArguments = false;
                        Creator = Argument => new ControlExpression(Location, ControlType.Break);
                        return true;
                    case "next":
                        TakeArguments = false;
                        Creator = Argument => new ControlExpression(Location, ControlType.Next);
                        return true;
                    case "redo":
                        TakeArguments = false;
                        Creator = Argument => new ControlExpression(Location, ControlType.Redo);
                        return true;
                    case "retry":
                        TakeArguments = false;
                        Creator = Argument => new ControlExpression(Location, ControlType.Retry);
                        return true;
                    case "return":
                        TakeArguments = true;
                        Creator = Arguments => {
                            Expression? Argument = Arguments.Length switch {
                                0 => null,
                                1 => Arguments[0],
                                _ => new ArrayExpression(Location, Arguments)
                            };
                            return new ControlExpression(Location, ControlType.Return, Argument);
                        };
                        return true;
                    case "yield":
                        TakeArguments = true;
                        Creator = Arguments => new YieldExpression(Location, Arguments);
                        return true;
                    case "super":
                        TakeArguments = true;
                        Creator = Arguments => new SuperExpression(Location, Arguments);
                        return true;
                    default:
                        TakeArguments = false;
                        Creator = null;
                        return false;
                };
            }
            for (int i = 0; i < Objects.Count; i++) {
                RubyObject? Object = Objects[i];

                if (Object is Token Token) {
                    // Control statement
                    if (GetControlStatement(Token.Value!, Token.Location, out bool TakeArgument, out Func<Expression[], Expression>? Creator)) {
                        // Remove control statement object
                        Objects.RemoveAt(i);

                        // Get argument if valid
                        Expression[] Arguments = System.Array.Empty<Expression>();
                        if (TakeArgument) {
                            // Take argument objects
                            List<RubyObject?> ArgumentObjects = Objects.GetIndexRange(i);
                            Objects.RemoveIndexRange(i);
                            // Parse arguments
                            Arguments = ParseCommaSeparatedExpressions(Location, ArgumentObjects);
                        }

                        // Insert control expression
                        Objects.Insert(i, Creator!(Arguments));
                    }
                }
            }
        }
        static void MatchAssignment(CodeLocation Location, List<RubyObject?> Objects) {
            for (int i = 0; i < Objects.Count; i++) {
                RubyObject? Object = Objects[i];

                if (Object is Token Token && Token.Type is TokenType.AssignmentOperator) {
                    // Assignment targets
                    ReferenceExpression[] Targets = ParseCommaSeparatedExpressions(Location, Objects.GetIndexRange(0, i - 1)).TryCast<ReferenceExpression>()
                        ?? throw new SyntaxError($"{Location}: expected reference for assignment");
                    // Assignment values
                    Expression[] Values = ParseCommaSeparatedExpressions(Location, Objects.GetIndexRange(i + 1));
                    // Remove assignment objects
                    Objects.Clear();

                    // Get compound assignment operator
                    string? CompoundOperator = null;
                    if (Token.Value is not "=") {
                        CompoundOperator = Token.Value![..^1];
                    }
                    // Create assignment value
                    Expression CreateValue(Expression Target, Expression Value) {
                        return CompoundOperator is not null
                            // Convert (a += b) to (a = a.+(b))
                            ? new MethodCallExpression(Target.Location, Target, CompoundOperator, new Expression[] { Value })
                            // Direct value
                            : Value;
                    }

                    // Replace identifier targets with constants or locals
                    for (int i2 = 0; i2 < Targets.Length; i2++) {
                        if (Targets[i2] is IdentifierExpression Identifier) {
                            Targets[i2] = Identifier.PossibleConstant
                                ? new ConstantExpression(Identifier.Location, Identifier.Name)
                                : new LocalExpression(Identifier.Location, Identifier.Name);
                        }
                    }

                    // = b
                    if (Targets.Length == 0) {
                        throw new SyntaxError($"{Token.Location}: expected variable before '{Token}'");
                    }
                    // a =
                    else if (Values.Length == 0) {
                        throw new SyntaxError($"{Token.Location}: expected value after '{Token}'");
                    }
                    // a = b
                    else if (Targets.Length == 1 && Values.Length == 1) {
                        Objects.Add(new AssignmentExpression(Targets[0], CreateValue(Targets[0], Values[0])));
                    }
                    // a, b = c, d
                    else if (Targets.Length == Values.Length) {
                        AssignmentExpression[] Assignments = new AssignmentExpression[Targets.Length];
                        for (int i2 = 0; i2 < Targets.Length; i2++) {
                            Assignments[i2] = new AssignmentExpression(Targets[i2], CreateValue(Targets[i2], Values[i2]));
                        }
                        Objects.Add(new MultiAssignmentExpression(Location, Assignments));
                    }
                    // a, b = c
                    else if (Values.Length == 1) {
                        if (CompoundOperator is not null) {
                            throw new SyntaxError($"{Location}: compound operator not valid for array expanding assignment");
                        }
                        Objects.Add(new ExpandAssignmentExpression(Location, Targets, Values[0]));
                    }
                    // a = b, c
                    else {
                        throw new SyntaxError($"{Token.Location}: assignment count mismatch");
                    }
                }
            }
        }
    }
}
