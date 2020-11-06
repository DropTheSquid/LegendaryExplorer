﻿using ME3Script.Analysis.Symbols;
using ME3Script.Compiling.Errors;
using ME3Script.Language.Tree;
using ME3Script.Language.Util;
using ME3Script.Utilities;
using System;
using System.Linq;
using ME3ExplorerCore.Helpers;
using ME3ExplorerCore.Unreal.BinaryConverters;
using static ME3ExplorerCore.Unreal.UnrealFlags;

namespace ME3Script.Analysis.Visitors
{
    public enum ValidationPass
    {
        TypesAndFunctionNamesAndStateNames,
        ClassAndStructMembersAndFunctionParams,
        BodyPass
    }

    public class ClassValidationVisitor : IASTVisitor
    {

        private readonly SymbolTable Symbols;
        private readonly MessageLog Log;
        private bool Success;

        public ValidationPass Pass;

        public ClassValidationVisitor(MessageLog log, SymbolTable symbols, ValidationPass pass)
        {
            Log = log ?? new MessageLog();
            Symbols = symbols;
            Success = true;
            Pass = pass;
        }

        private bool Error(string msg, SourcePosition start = null, SourcePosition end = null)
        {
            Log.LogError(msg, start, end);
            Success = false;
            return false;
        }

        public bool VisitNode(Class node)
        {
            switch (Pass)
            {
                case ValidationPass.TypesAndFunctionNamesAndStateNames:
                {
                    // TODO: allow duplicate names as long as its in different packages!
                    if (node.Name != "Object")//validating Object is a special case, as it is the base class for all classes
                    {
                        //ADD CLASSNAME TO SYMBOLS BEFORE VALIDATION pass!
                        //if (!Symbols.TryAddType(node))
                        //{
                        //    return Error($"A class named '{node.Name}' already exists!", node.StartPos, node.EndPos);
                        //}
                        node.Parent.Outer = node;
                        if (!Symbols.TryResolveType(ref node.Parent))
                            return Error($"No parent class named '{node.Parent.Name}' found!", node.Parent.StartPos, node.Parent.EndPos);
                        if (node.Parent.Type != ASTNodeType.Class)
                            return Error($"Parent named '{node.Parent.Name}' is not a class!", node.Parent.StartPos, node.Parent.EndPos);

                        if (node._outerClass != null)
                        {
                            node._outerClass.Outer = node;
                            if (!Symbols.TryResolveType(ref node._outerClass))
                                return Error($"No outer class named '{node._outerClass.Name}' found!", node._outerClass.StartPos, node._outerClass.EndPos);
                            if (node.OuterClass.Type != ASTNodeType.Class)
                                return Error($"Outer named '{node.OuterClass.Name}' is not a class!", node.OuterClass.StartPos, node.OuterClass.EndPos);
                            if (node.Parent.Name == "Actor" && !node.OuterClass.Name.Equals("Object", StringComparison.OrdinalIgnoreCase))
                                return Error("Classes extending 'Actor' can not be inner classes!", node.OuterClass.StartPos, node.OuterClass.EndPos);
                        }

                        for (int i = 0; i < node.Interfaces.Count; i++)
                        {
                            VariableType nodeInterface = node.Interfaces[i];
                            if (!Symbols.TryResolveType(ref nodeInterface, true))
                            {
                                return Error($"No outer class named '{nodeInterface.Name}' found!", nodeInterface.StartPos, nodeInterface.EndPos);
                            }
                            node.Interfaces[i] = nodeInterface;
                        }

                        //specifier validation
                        if (string.Equals(node.ConfigName, "inherit", StringComparison.OrdinalIgnoreCase) && !((Class)node.Parent).Flags.Has(EClassFlags.Config))
                        {
                            return Error($"Cannot inherit config filename from parent class ({node.Parent.Name}) which is not marked as config!", node.StartPos);
                        }
                        //TODO:propagate/check inheritable class flags from parent and implemented interfaces
                        if (node.IsNative && !((Class)node.Parent).IsNative)
                        {
                            return Error($"A native class cannot inherit from a non-native class!", node.StartPos);
                        }
                        Symbols.GoDirectlyToStack(((Class)node.Parent).GetInheritanceString());
                        Symbols.PushScope(node.Name);
                    }



                    //register all the types this class declares
                    foreach (VariableType type in node.TypeDeclarations)
                    {
                        type.Outer = node;
                        Success &= type.AcceptVisitor(this);
                    }

                    //register all the function names (do this here so that delegates will resolve correctly)
                    foreach (Function func in node.Functions)
                    {
                        func.Outer = node;
                        Success &= func.AcceptVisitor(this);
                    }

                    //register all state names (do this here so that states can extend states that are declared later in the class)
                    foreach (State state in node.States)
                    {
                        state.Outer = node;
                        Success &= state.AcceptVisitor(this);
                    }

                    Symbols.RevertToObjectStack();//pops scope until we're in the 'object' scope

                    return Success;
                }
                case ValidationPass.ClassAndStructMembersAndFunctionParams:
                {
                    if (node.Name != "Object")
                    {
                        if (((Class)node.Parent).SameAsOrSubClassOf(node.Name)) // TODO: not needed due to no forward declarations?
                        {
                            return Error($"Extending from '{node.Parent.Name}' causes circular extension!", node.Parent.StartPos, node.Parent.EndPos);
                        }
                        if (!((Class)node.OuterClass).SameAsOrSubClassOf(((Class)node.Parent).OuterClass.Name))
                        {
                            return Error("Outer class must be a sub-class of the parents outer class!", node.OuterClass.StartPos, node.OuterClass.EndPos);
                        }
                        Symbols.GoDirectlyToStack(((Class)node.Parent).GetInheritanceString());
                        string outerScope = null;
                        if (node.OuterClass != null && !string.Equals(node.OuterClass.Name, "Object", StringComparison.OrdinalIgnoreCase))
                        {
                            outerScope = ((Class)node.OuterClass).GetInheritanceString();
                        }

                        Symbols.PushScope(node.Name); //, outerScope);
                    }

                    //second pass over structs to resolve their members
                    foreach (Struct type in node.TypeDeclarations.OfType<Struct>())
                    {
                        Success &= type.AcceptVisitor(this);
                    }

                    //resolve instance variables
                    foreach (VariableDeclaration decl in node.VariableDeclarations)
                    {
                        decl.Outer = node;
                        Success &= decl.AcceptVisitor(this);
                    }

                    if (node.Name != "Object")
                    {
                        Symbols.TryGetType("Object", out Class objectClass);
                        Symbols.AddSymbol("Class", new VariableDeclaration(new ClassType(node), EPropertyFlags.Const | EPropertyFlags.Native | EPropertyFlags.EditConst, "Class")
                        {
                            Outer = objectClass
                        });
                        Symbols.AddSymbol("Outer", new VariableDeclaration(node.OuterClass, EPropertyFlags.Const | EPropertyFlags.Native | EPropertyFlags.EditConst, "Outer")
                        {
                            Outer = objectClass
                        });
                    }

                    //second pass over functions to resolve parameters (TODO: and body)
                    foreach (Function func in node.Functions)
                    {
                        Success &= func.AcceptVisitor(this);
                    }

                    //second pass over states to resolve 
                    foreach (State state in node.States)
                    {
                        Success &= state.AcceptVisitor(this);
                    }

                    Symbols.RevertToObjectStack();//pops scope until we're in the 'object' scope

                    node.Declaration = node;
                    return Success;
                }
                case ValidationPass.BodyPass:
                {
                    if (node.SameAsOrSubClassOf("Interface"))
                    {
                        node.Flags |= EClassFlags.Interface;
                        node.PropertyType = EPropertyType.Interface;
                    }

                    //third pass over structs to check for circular inheritance chains
                    foreach (Struct type in node.TypeDeclarations.OfType<Struct>())
                    {
                        Success &= type.AcceptVisitor(this);
                    }


                    foreach (Function func in node.Functions)
                    {
                        //if the return type is > 64 bytes, it can't be allocated on the stack.
                        func.RetValNeedsDestruction |= (func.ReturnType?.Size ?? 0) > 64;
                    }
                    return Success;
                }
                default:
                    return Success;
            }
        }


        public bool VisitNode(VariableDeclaration node)
        {
            node.VarType.Outer = node;
            if (!Symbols.TryResolveType(ref node.VarType))
            {
                return Error($"No type named '{node.VarType.Name}' exists!", node.VarType.StartPos, node.VarType.EndPos);
            }

            if (Symbols.SymbolExistsInCurrentScope(node.Name))
            {
                return Error($"A member named '{node.Name}' already exists in this {node.Outer.Type}!", node.StartPos, node.EndPos);
            }
            Symbols.AddSymbol(node.Name, node);


            return Success;
        }

        public bool VisitNode(VariableType node)
        {
            // This should never be called.
            throw new NotImplementedException();
        }

        public bool VisitNode(DynamicArrayType node)
        {
            throw new NotImplementedException();
        }

        public bool VisitNode(StaticArrayType node)
        {
            throw new NotImplementedException();
        }

        public bool VisitNode(DelegateType node)
        {
            throw new NotImplementedException();
        }
        public bool VisitNode(ClassType node)
        {
            throw new NotImplementedException();
        }

        public bool VisitNode(Struct node)
        {
            if (Pass == ValidationPass.TypesAndFunctionNamesAndStateNames)
            {
                if (!Symbols.TryAddType(node))
                {
                    //Structs do not have to be globally unique, but they do have to be unique within a scope
                    if (((IObjectType)node.Outer).TypeDeclarations.Any(decl => decl != node && decl.Name.CaseInsensitiveEquals(node.Name)))
                    {
                        return Error($"A type named '{node.Name}' already exists in this {node.Outer.GetType().Name.ToLower()}!", node.StartPos, node.EndPos);
                    }
                }

                Symbols.PushScope(node.Name);

                //register types of inner structs
                foreach (VariableType typeDeclaration in node.TypeDeclarations)
                {
                    typeDeclaration.Outer = node;
                    Success &= typeDeclaration.AcceptVisitor(this);
                }

                Symbols.PopScope();

                return Success;
            }

            if (Pass == ValidationPass.ClassAndStructMembersAndFunctionParams)
            {
                string parentScope = null;
                if (node.Parent != null)
                {
                    node.Parent.Outer = node;
                    if (!Symbols.TryResolveType(ref node.Parent))
                    {
                        return Error($"No parent struct named '{node.Parent.Name}' found!", node.Parent.StartPos, node.Parent.EndPos);
                    }

                    if (node.Parent.Type != ASTNodeType.Struct)
                        return Error($"Parent named '{node.Parent.Name}' is not a struct!", node.Parent.StartPos, node.Parent.EndPos);

                    parentScope = $"{NodeUtils.GetContainingClass(node.Parent).GetInheritanceString()}.{node.Parent.Name}";
                }

                Symbols.PushScope(node.Name, parentScope);

                //second pass for inner struct members
                foreach (VariableType typeDeclaration in node.TypeDeclarations)
                {
                    Success &= typeDeclaration.AcceptVisitor(this);
                }

                // TODO: can all types of variable declarations be supported in a struct?
                // what does the parser let through?
                foreach (VariableDeclaration decl in node.VariableDeclarations)
                {
                    decl.Outer = node;
                    Success = Success && decl.AcceptVisitor(this);
                }

                Symbols.PopScope();

                node.Declaration = node;

                return Success;
            }

            if (Pass == ValidationPass.BodyPass)
            {
                if (node.Parent != null && ((Struct)node.Parent).SameOrSubStruct(node.Name))
                    return Error($"Extending from '{node.Parent.Name}' causes circular extension!", node.Parent.StartPos, node.Parent.EndPos);
                //TODO
                return Success;
            }
            return Success;
        }

        public bool VisitNode(Enumeration node)
        {
            if (!Symbols.TryAddType(node))
            {
                //Enums do not have to be globally unique, but they do have to be unique within a scope
                if (((IObjectType)node.Outer).TypeDeclarations.Any(decl => decl != node && decl.Name.CaseInsensitiveEquals(node.Name)))
                {
                    return Error($"A type named '{node.Name}' already exists in this {node.Outer.GetType().Name.ToLower()}!", node.StartPos, node.EndPos);
                }
            }

            Symbols.PushScope(node.Name);

            foreach (EnumValue enumVal in node.Values)
            {
                enumVal.Outer = node;
                Symbols.AddSymbol(enumVal.Name, enumVal);
            }

            Symbols.PopScope();

            // Add enum values at the class scope so they can be used without being explicitly qualified.
            foreach (EnumValue enumVal in node.Values)
                Symbols.TryAddSymbol(enumVal.Name, enumVal);

            node.Declaration = node;

            return Success;
        }

        public bool VisitNode(Const node)
        {
            if (!Symbols.TryAddType(node))
            {
                //Consts do not have to be globally unique, but they do have to be unique within a scope
                if (((IObjectType)node.Outer).TypeDeclarations.Any(decl => decl != node && decl.Name.CaseInsensitiveEquals(node.Name)))
                {
                    return Error($"A type named '{node.Name}' already exists in this {node.Outer.GetType().Name.ToLower()}!", node.StartPos, node.EndPos);
                }
            }


            node.Declaration = node;

            return Success;
        }

        public bool VisitNode(Function node)
        {
            if (Pass == ValidationPass.TypesAndFunctionNamesAndStateNames)
            {
                if (Symbols.SymbolExistsInCurrentScope(node.Name))
                    return Error($"The name '{node.Name}' is already in use in this class!", node.StartPos, node.EndPos);

                Symbols.AddSymbol(node.Name, node);
                return Success;
            }

            if (Pass == ValidationPass.ClassAndStructMembersAndFunctionParams)
            {
                if (node.ReturnType != null && !Symbols.TryResolveType(ref node.ReturnType))
                {
                    return Error($"No type named '{node.ReturnType.Name}' exists!", node.ReturnType.StartPos, node.ReturnType.EndPos);
                }

                Symbols.PushScope(node.Name);
                foreach (FunctionParameter param in node.Parameters)
                {
                    param.Outer = node;
                    Success = Success && param.AcceptVisitor(this);
                }
                Symbols.PopScope();

                if (Success == false)
                    return Error("Error in function parameters.", node.StartPos, node.EndPos);

                string parentScope = null;
                Class containingClass = NodeUtils.GetContainingClass(node);
                if (node.Outer.Type == ASTNodeType.State)
                {
                    parentScope = containingClass.Name;
                }
                else if (containingClass.Parent != null)
                {
                    parentScope = containingClass.Parent.Name;
                }

                if (parentScope != null && Symbols.TryGetSymbolInScopeStack(node.Name, out ASTNode func, parentScope) // override functions in parent classes only (or current class if its a state)
                                        && func.Type == ASTNodeType.Function)
                {   // If there is a function with this name that we should override, validate the new functions declaration
                    Function original = (Function)func;
                    if (original.Flags.Has(FunctionFlags.Final))
                        return Error($"{node.Name} overrides a function in a parent class, but the parent function is marked as final!", node.StartPos, node.EndPos);
                    if (!Equals(node.ReturnType, original.ReturnType))
                        return Error($"{node.Name} overrides a function in a parent class, but the functions do not have the same return types!", node.StartPos, node.EndPos);
                    if (node.Parameters.Count != original.Parameters.Count)
                        return Error($"{node.Name} overrides a function in a parent class, but the functions do not have the same number of parameters!", node.StartPos, node.EndPos);
                    for (int n = 0; n < node.Parameters.Count; n++)
                    {
                        if (node.Parameters[n].Type != original.Parameters[n].Type)
                            return Error($"{node.Name} overrides a function in a parent class, but the functions do not have the same parameter types!", node.StartPos, node.EndPos);
                    }
                }

                return Success;
            }
            return Success;
        }

        public bool VisitNode(FunctionParameter node)
        {
            node.VarType.Outer = node;
            if (!Symbols.TryResolveType(ref node.VarType))
            {
                return Error($"No type named '{node.VarType.Name}' exists in this scope!", node.VarType.StartPos, node.VarType.EndPos);
            }

            if (Symbols.SymbolExistsInCurrentScope(node.Name))
            {
                return Error($"A parameter named '{node.Name}' already exists in this function!", 
                             node.StartPos, node.EndPos);
            }

            Symbols.AddSymbol(node.Name, node);
            return Success;
        }

        public bool VisitNode(State node)
        {
            if (Pass == ValidationPass.TypesAndFunctionNamesAndStateNames)
            {
                if (Symbols.SymbolExistsInCurrentScope(node.Name))
                    return Error($"The name '{node.Name}' is already in use in this class!", node.StartPos, node.EndPos);
                Symbols.AddSymbol(node.Name, node);
                return Success;
            }

            if (Pass == ValidationPass.ClassAndStructMembersAndFunctionParams)
            {
                bool overrides = Symbols.TryGetSymbolInScopeStack(node.Name, out ASTNode overrideState, NodeUtils.GetParentClassScope(node))
                              && overrideState.Type == ASTNodeType.State;

                if (node.Parent != null)
                {
                    if (overrides)
                        return Error("A state is not allowed to both override a parent class's state and extend another state at the same time!", node.StartPos, node.EndPos);

                    if (!Symbols.TryGetSymbolFromCurrentScope(node.Parent.Name, out ASTNode parent))
                        Error($"No parent state named '{node.Parent.Name}' found in the current class!", node.Parent.StartPos, node.Parent.EndPos);
                    if (parent != null)
                    {
                        if (parent.Type != ASTNodeType.State)
                            Error($"Parent named '{node.Parent.Name}' is not a state!", node.Parent.StartPos, node.Parent.EndPos);
                        else
                            node.Parent = parent as State;
                    }
                }

                int numFuncs = node.Functions.Count;
                Symbols.PushScope(node.Name);
                foreach (Function ignore in node.Ignores)
                {
                    if (Symbols.TryGetSymbol(ignore.Name, out ASTNode original, "") && original.Type == ASTNodeType.Function)
                    {
                        Function header = (Function)original;
                        Function emptyOverride = new Function(header.Name, header.Flags, header.ReturnType, new CodeBody(), header.Parameters, ignore.StartPos, ignore.EndPos);
                        node.Functions.Add(emptyOverride);
                        Symbols.AddSymbol(emptyOverride.Name, emptyOverride);
                    }
                    else //TODO: really ought to throw error, but PlayerController.PlayerWaiting.Jump is like this. Find alternate way of handling this?
                    {
                        node.Functions.Add(ignore);
                        Symbols.AddSymbol(ignore.Name, ignore);
                    }
                }

                foreach (Function func in node.Functions.GetRange(0, numFuncs))
                {
                    func.Outer = node;
                    Success = Success && func.AcceptVisitor(this);
                }
                //TODO: check functions overrides:
                //if the state overrides another state, we should be in that scope as well whenh we check overrides maybe?
                //if the state has a parent state, we should be in that scope
                //this is a royal mess, check that ignores also look-up from parent/overriding states as we are not sure if symbols are in the scope

                // if the state extends a parent state, use that as outer in the symbol lookup
                // if the state overrides another state, use that as outer
                // both of the above should apply to functions as well as ignores.

                //TODO: state code/labels

                Symbols.PopScope();
                return Success;
            }
            return Success;
        }

        #region Unused
        public bool VisitNode(CodeBody node)
        { throw new NotImplementedException(); }
        public bool VisitNode(Label node)
        { throw new NotImplementedException(); }

        public bool VisitNode(VariableIdentifier node)
        { throw new NotImplementedException(); }
        public bool VisitNode(EnumValue node)
        { throw new NotImplementedException(); }

        public bool VisitNode(DoUntilLoop node)
        { throw new NotImplementedException(); }
        public bool VisitNode(ForLoop node)
        { throw new NotImplementedException(); }
        public bool VisitNode(ForEachLoop node)
        { throw new NotImplementedException(); }
        public bool VisitNode(WhileLoop node)
        { throw new NotImplementedException(); }

        public bool VisitNode(SwitchStatement node)
        { throw new NotImplementedException(); }
        public bool VisitNode(CaseStatement node)
        { throw new NotImplementedException(); }
        public bool VisitNode(DefaultCaseStatement node)
        { throw new NotImplementedException(); }

        public bool VisitNode(AssignStatement node)
        { throw new NotImplementedException(); }
        public bool VisitNode(AssertStatement node)
        { throw new NotImplementedException(); }
        public bool VisitNode(BreakStatement node)
        { throw new NotImplementedException(); }
        public bool VisitNode(ContinueStatement node)
        { throw new NotImplementedException(); }
        public bool VisitNode(IfStatement node)
        { throw new NotImplementedException(); }
        public bool VisitNode(ReturnStatement node)
        { throw new NotImplementedException(); }
        public bool VisitNode(ReturnNothingStatement node)
        { throw new NotImplementedException(); }
        public bool VisitNode(StopStatement node)
        { throw new NotImplementedException(); }
        public bool VisitNode(StateGoto node)
        { throw new NotImplementedException(); }
        public bool VisitNode(Goto node)
        { throw new NotImplementedException(); }

        public bool VisitNode(ExpressionOnlyStatement node)
        { throw new NotImplementedException(); }

        public bool VisitNode(InOpReference node)
        { throw new NotImplementedException(); }
        public bool VisitNode(PreOpReference node)
        { throw new NotImplementedException(); }
        public bool VisitNode(PostOpReference node)
        { throw new NotImplementedException(); }
        public bool VisitNode(StructComparison node)
        { throw new NotImplementedException(); }
        public bool VisitNode(DelegateComparison node)
        { throw new NotImplementedException(); }
        public bool VisitNode(NewOperator node)
        { throw new NotImplementedException(); }

        public bool VisitNode(FunctionCall node)
        { throw new NotImplementedException(); }

        public bool VisitNode(DelegateCall node)
        { throw new NotImplementedException(); }

        public bool VisitNode(ArraySymbolRef node)
        { throw new NotImplementedException(); }
        public bool VisitNode(CompositeSymbolRef node)
        { throw new NotImplementedException(); }
        public bool VisitNode(SymbolReference node)
        { throw new NotImplementedException(); }
        public bool VisitNode(DefaultReference node)
        { throw new NotImplementedException(); }
        public bool VisitNode(DynArrayLength node)
        { throw new NotImplementedException(); }

        public bool VisitNode(DynArrayAdd node)
        {
            throw new NotImplementedException();
        }

        public bool VisitNode(DynArrayAddItem node)
        {
            throw new NotImplementedException();
        }

        public bool VisitNode(DynArrayInsert node)
        {
            throw new NotImplementedException();
        }

        public bool VisitNode(DynArrayInsertItem node)
        {
            throw new NotImplementedException();
        }

        public bool VisitNode(DynArrayRemove node)
        {
            throw new NotImplementedException();
        }

        public bool VisitNode(DynArrayRemoveItem node)
        {
            throw new NotImplementedException();
        }

        public bool VisitNode(DynArrayFind node)
        {
            throw new NotImplementedException();
        }

        public bool VisitNode(DynArrayFindStructMember node)
        {
            throw new NotImplementedException();
        }

        public bool VisitNode(DynArraySort node)
        {
            throw new NotImplementedException();
        }
        public bool VisitNode(DynArrayIterator node)
        {
            throw new NotImplementedException();
        }

        public bool VisitNode(BooleanLiteral node)
        { throw new NotImplementedException(); }
        public bool VisitNode(FloatLiteral node)
        { throw new NotImplementedException(); }
        public bool VisitNode(IntegerLiteral node)
        { throw new NotImplementedException(); }
        public bool VisitNode(NameLiteral node)
        { throw new NotImplementedException(); }
        public bool VisitNode(StringLiteral node)
        { throw new NotImplementedException(); }
        public bool VisitNode(StringRefLiteral node)
        { throw new NotImplementedException(); }
        public bool VisitNode(StructLiteral node)
        { throw new NotImplementedException(); }
        public bool VisitNode(DynamicArrayLiteral node)
        { throw new NotImplementedException(); }
        public bool VisitNode(ObjectLiteral node)
        { throw new NotImplementedException(); }
        public bool VisitNode(VectorLiteral node)
        { throw new NotImplementedException(); }
        public bool VisitNode(RotatorLiteral node)
        { throw new NotImplementedException(); }
        public bool VisitNode(NoneLiteral node)
        { throw new NotImplementedException(); }

        public bool VisitNode(ConditionalExpression node)
        { throw new NotImplementedException(); }
        public bool VisitNode(CastExpression node)
        { throw new NotImplementedException(); }

        public bool VisitNode(DefaultPropertiesBlock node)
        { throw new NotImplementedException(); }
        public bool VisitNode(Subobject node)
        { throw new NotImplementedException(); }
        #endregion
    }
}
