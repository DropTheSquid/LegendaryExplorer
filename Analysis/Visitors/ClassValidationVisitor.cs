﻿using ME3Script.Analysis.Symbols;
using ME3Script.Compiling.Errors;
using ME3Script.Language.Tree;
using ME3Script.Utilities;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ME3Script.Analysis.Visitors
{
    public class ClassValidationVisitor : IASTVisitor
    {
        private SymbolTable Symbols;
        private MessageLog Log;
        private bool Success;

        public ClassValidationVisitor(MessageLog log, SymbolTable symbols)
        {
            Log = log;
            Symbols = symbols;
            Success = true;
        }

        private bool Error(String msg, SourcePosition start = null, SourcePosition end = null)
        {
            Log.LogError(msg, start, end);
            Success = false;
            return false;
        }

        public bool VisitNode(Class node)
        {
            // TODO: allow duplicate names as long as its in different packages!
            if (Symbols.SymbolExists(node.Name, ""))
                return Error("A class named '" + node.Name + "' already exists!", node.StartPos, node.EndPos);

            Symbols.AddSymbol(node.Name, node);
            Symbols.PushScope(node.Name);

            ASTNode parent;
            if (!Symbols.TryGetSymbol(node.Parent.Name, out parent, ""))
                Error("No parent class named '" + node.Parent.Name + "' found!", node.Parent.StartPos, node.Parent.EndPos);
            if (parent != null)
            {
                if (parent.Type != ASTNodeType.Class)
                    Error("Parent named '" + node.Parent.Name + "' is not a class!", node.Parent.StartPos, node.Parent.EndPos);
                else if ((parent as Class).IsClassOrSubClass(node.Name))
                    Error("Extending from '" + node.Parent.Name + "' causes circular extension!", node.Parent.StartPos, node.Parent.EndPos);
                else
                    node.Parent = parent as Class;
            }

            ASTNode outer;
            if (node.OuterClass != null)
            {
                if (!Symbols.TryGetSymbol(node.OuterClass.Name, out outer, ""))
                    Error("No outer class named '" + node.OuterClass.Name + "' found!", node.OuterClass.StartPos, node.OuterClass.EndPos);
                else if (outer.Type != ASTNodeType.Class)
                    Error("Outer named '" + node.OuterClass.Name + "' is not a class!", node.OuterClass.StartPos, node.OuterClass.EndPos);
                else if (node.Parent.Name == "Actor")
                    Error("Classes extending 'Actor' can not be inner classes!", node.OuterClass.StartPos, node.OuterClass.EndPos);
                else if (!(outer as Class).IsClassOrSubClass((node.Parent as Class).OuterClass.Name))
                    Error("Outer class must be a sub-class of the parents outer class!", node.OuterClass.StartPos, node.OuterClass.EndPos);
            }
            else
            {
                outer = (node.Parent as Class).OuterClass;
            }
            node.OuterClass = outer as Class;

            // TODO(?) validate class specifiers more than the initial parsing?

            // Messy, should probably be refactored to happen in the parsing state later.
            // Though this way we avoid a LOT of extra cruff there.
            foreach (VariableType type in node.TypeDeclarations)
                type.Outer = node;
            foreach (VariableDeclaration decl in node.VariableDeclarations)
                decl.Outer = node;
            foreach (VariableDeclaration decl in node.VariableDeclarations)
                decl.Outer = node;
            foreach (OperatorDeclaration op in node.Operators)
                op.Outer = node;
            foreach (Function func in node.Functions)
                func.Outer = node;
            foreach (State state in node.States)
                state.Outer = node;

            return Success;
        }


        public bool VisitNode(VariableDeclaration node)
        {
            throw new NotImplementedException();
        }

        public bool VisitNode(VariableType node)
        {
            throw new NotImplementedException();
        }

        public bool VisitNode(Struct node)
        {
            String classOuterScope = ((node.Outer as Class).OuterClass as Class).GetInheritanceString();

            if (Symbols.SymbolExists(node.Name, classOuterScope))
                return Error("A struct named '" + node.Name + "' already exists!", node.StartPos, node.EndPos);


            return Success;
        }

        public bool VisitNode(Enumeration node)
        {
            throw new NotImplementedException();
        }

        public bool VisitNode(Function node)
        {
            throw new NotImplementedException();
        }

        public bool VisitNode(State node)
        {
            throw new NotImplementedException();
        }

        public bool VisitNode(OperatorDeclaration node)
        {
            throw new NotImplementedException();
        }
    }
}
