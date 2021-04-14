﻿using System;
using System.Collections.Generic;
using System.Linq;
using ME3ExplorerCore.Gammtek.Extensions;
using ME3ExplorerCore.Misc;
using ME3ExplorerCore.Packages;
using ME3ExplorerCore.Helpers;
using ME3ExplorerCore.Packages.CloningImportingAndRelinking;
using ME3ExplorerCore.Unreal;
using ME3ExplorerCore.Unreal.BinaryConverters;
using ME3ExplorerCore.UnrealScript.Analysis.Symbols;
using ME3ExplorerCore.UnrealScript.Analysis.Visitors;
using ME3ExplorerCore.UnrealScript.Language.Tree;
using ME3ExplorerCore.UnrealScript.Utilities;
using Unrealscript.Language.ByteCode;
using Unrealscript.Language.Util;

namespace Unrealscript.Compiling
{
    public class ByteCodeCompilerVisitor : BytecodeWriter, IASTVisitor
    {
        private readonly UStruct Target;
        private IContainsByteCode CompilationUnit;
        private readonly IEntry ContainingClass;

        readonly CaseInsensitiveDictionary<UProperty> parameters = new();
        readonly CaseInsensitiveDictionary<UProperty> locals = new();

        private bool inAssignTarget;
        private bool inIteratorCall;
        private bool useInstanceDelegate;
        private SkipPlaceholder iteratorCallSkip;

        private readonly Dictionary<Label, List<JumpPlaceholder>> LabelJumps = new();

        [Flags]
        private enum NestType
        {
            Loop = 1,
            For = 2 | Loop,
            While = 4 | Loop,
            DoUntil = 8 | Loop,
            ForEach = 16 | Loop,
            Switch = 32,
        }
        private class Nest : List<JumpPlaceholder>
        {
            public readonly NestType Type;
            public JumpPlaceholder CaseJumpPlaceholder;
            public VariableType SwitchType;

            public Nest(NestType type)
            {
                Type = type;
            }

            public void SetPositionFor(JumpType jumpType)
            {
                foreach (JumpPlaceholder jumpPlaceholder in this.Where(j => j.Type == jumpType))
                {
                    jumpPlaceholder.End();
                }
            }
        }

        private readonly Stack<Nest> Nests = new();

        public ByteCodeCompilerVisitor(UStruct target) : base(target.Export.FileRef)
        {
            Target = target;
            IEntry containingClass = Target.Export;
            while (containingClass.ClassName != "Class")
            {
                containingClass = containingClass.Parent;
            }

            ContainingClass = containingClass;
        }

        public void Compile(Function func)
        {
            if (Target is UFunction uFunction)
            {
                CompilationUnit = func;
                var nextItem = uFunction.Children;
                UProperty returnValue = null;
                while (uFunction.Export.FileRef.TryGetUExport(nextItem, out ExportEntry nextChild))
                {
                    var objBin = ObjectBinary.From(nextChild);
                    switch (objBin)
                    {
                        case UProperty uProperty:
                            if (uProperty.PropertyFlags.HasFlag(UnrealFlags.EPropertyFlags.ReturnParm))
                            {
                                returnValue = uProperty;
                            }
                            else if (uProperty.PropertyFlags.HasFlag(UnrealFlags.EPropertyFlags.Parm))
                            {
                                parameters.Add(nextChild.ObjectName.Instanced, uProperty);
                            }
                            else
                            {
                                locals.Add(nextChild.ObjectName.Instanced, uProperty);
                            }
                            nextItem = uProperty.Next;
                            break;
                        default:
                            nextItem = 0;
                            break;
                    }
                }

                foreach (FunctionParameter parameter in func.Parameters.Where(param => param.IsOptional))
                {
                    if (parameter.DefaultParameter is Expression expr)
                    {
                        WriteOpCode(OpCodes.DefaultParmValue);

                        using (WriteSkipPlaceholder())
                        {
                            Emit(AddConversion(parameter.VarType, expr));
                            WriteOpCode(OpCodes.EndParmValue);
                        }
                    }
                    else
                    {
                        WriteOpCode(OpCodes.Nothing);
                    }
                }

                Emit(func.Body);

                WriteOpCode(OpCodes.Return);
                if (returnValue != null)
                {
                    WriteOpCode(OpCodes.ReturnNullValue);
                    WriteObjectRef(returnValue.Export);
                }
                else
                {
                    WriteOpCode(OpCodes.Nothing);
                }

                WriteOpCode(OpCodes.EndOfScript);

                foreach ((Label label, List<JumpPlaceholder> jumpPlaceholders) in LabelJumps)
                {
                    foreach (JumpPlaceholder jumpPlaceholder in jumpPlaceholders)
                    {
                        jumpPlaceholder.End(label.StartOffset);
                    }
                }

                Target.ScriptBytecodeSize = GetMemLength();
                Target.ScriptBytes = GetByteCode();
                Target.Export.WriteBinary(Target);
            }
            else
            {
                throw new Exception("Cannot compile a function to a state!");
            }
        }


        public void Compile(State state)
        {
            if (Target is UState uState)
            {
                CompilationUnit = state;
                uState.LabelTableOffset = 0;
                Emit(state.Body);

                WriteOpCode(OpCodes.Stop);

                if (state.Labels.Any())
                {
                    int paddingNeeded = (Position + 1).Align(4) - (Position + 1);
                    for (; paddingNeeded > 0; paddingNeeded--)
                    {
                        WriteOpCode(OpCodes.Nothing);
                    }
                    WriteOpCode(OpCodes.LabelTable);
                    uState.LabelTableOffset = Position;
                    foreach (Label label in state.Labels)
                    {
                        WriteName(label.Name);
                        WriteInt(label.StartOffset);
                    }
                    WriteName("None");
                    WriteInt(ushort.MaxValue);
                }

                WriteOpCode(OpCodes.EndOfScript);

                foreach ((Label label, List<JumpPlaceholder> jumpPlaceholders) in LabelJumps)
                {
                    foreach (JumpPlaceholder jumpPlaceholder in jumpPlaceholders)
                    {
                        jumpPlaceholder.End(label.StartOffset);
                    }
                }

                Target.ScriptBytecodeSize = GetMemLength();
                Target.ScriptBytes = GetByteCode();
                Target.Export.WriteBinary(Target);
            }
            else
            {
                throw new Exception("Cannot compile a state to a function!");
            }
        }


        public bool VisitNode(CodeBody node)
        {
            foreach (Statement statement in node.Statements)
            {
                Emit(statement);
            }
            return true;
        }

        public bool VisitNode(DoUntilLoop node)
        {
            var loopStartPos = Position;
            Nests.Push(new Nest(NestType.DoUntil));
            Emit(node.Body);
            Nest nest = Nests.Pop();
            nest.SetPositionFor(JumpType.Continue);
            WriteOpCode(OpCodes.JumpIfNot);
            WriteUShort(loopStartPos);
            Emit(node.Condition);
            nest.SetPositionFor(JumpType.Break);
            return true;
        }

        public bool VisitNode(ForLoop node)
        {
            node.Init?.AcceptVisitor(this);
            var loopStartPos = Position;
            JumpPlaceholder endJump = EmitConditionalJump(node.Condition);
            Nests.Push(new Nest(NestType.For));
            Emit(node.Body);
            Nest nest = Nests.Pop();
            nest.SetPositionFor(JumpType.Continue);
            node.Update?.AcceptVisitor(this);
            WriteOpCode(OpCodes.Jump);
            WriteUShort(loopStartPos);
            endJump.End();
            nest.SetPositionFor(JumpType.Break);
            return true;
        }

        public bool VisitNode(ForEachLoop node)
        {
            if (node.IteratorCall is DynArrayIterator || node.IteratorCall is CompositeSymbolRef c && c.InnerSymbol is DynArrayIterator)
            {
                WriteOpCode(OpCodes.DynArrayIterator);
            }
            else
            {
                WriteOpCode(OpCodes.Iterator);
            }

            inIteratorCall = true;
            Emit(node.IteratorCall);
            inIteratorCall = false;
            var loopSkip = iteratorCallSkip;
            var endJump = WriteJumpPlaceholder(JumpType.Break);
            Nests.Push(new Nest(NestType.ForEach));
            Emit(node.Body);
            Nest nest = Nests.Pop();
            WriteOpCode(OpCodes.IteratorNext);
            endJump.End();
            nest.SetPositionFor(JumpType.Break);
            if (Game <= MEGame.ME2)
            {
                loopSkip?.End();
                WriteOpCode(OpCodes.IteratorPop);
            }
            else
            {
                WriteOpCode(OpCodes.IteratorPop);
                loopSkip?.End();
            }
            return true;
        }

        public bool VisitNode(WhileLoop node)
        {
            var loopStartPos = Position;
            JumpPlaceholder endJump = EmitConditionalJump(node.Condition);
            Nests.Push(new Nest(NestType.While));
            Emit(node.Body);
            Nest nest = Nests.Pop();
            nest.SetPositionFor(JumpType.Continue);
            WriteOpCode(OpCodes.Jump);
            WriteUShort(loopStartPos);
            endJump.End();
            nest.SetPositionFor(JumpType.Break);
            return true;
        }

        public bool VisitNode(SwitchStatement node)
        {
            WriteOpCode(OpCodes.Switch);
            EmitVariableSize(node.Expression);
            Emit(node.Expression);
            Nests.Push(new Nest(NestType.Switch) { SwitchType = node.Expression.ResolveType() });
            Emit(node.Body);
            Nest nest = Nests.Pop();
            nest.CaseJumpPlaceholder?.End();
            nest.SetPositionFor(JumpType.Break);
            return true;
        }

        public bool VisitNode(CaseStatement node)
        {
            Nest nest = Nests.Peek();
            nest.CaseJumpPlaceholder?.End();
            WriteOpCode(OpCodes.Case);
            nest.CaseJumpPlaceholder = WriteJumpPlaceholder(JumpType.Case);
            Emit(AddConversion(nest.SwitchType, node.Value));
            return true;
        }

        public bool VisitNode(DefaultCaseStatement node)
        {
            Nest nest = Nests.Peek();
            nest.CaseJumpPlaceholder?.End();
            nest.CaseJumpPlaceholder = null;
            WriteOpCode(OpCodes.Case);
            WriteUShort(0xFFFF);
            return true;
        }

        public bool VisitNode(AssignStatement node)
        {
            VariableType targetType = node.Target.ResolveType();
            if (targetType == SymbolTable.BoolType)
            {
                WriteOpCode(OpCodes.LetBool);
            }
            else if (targetType is DelegateType)
            {
                WriteOpCode(OpCodes.LetDelegate);
            }
            else
            {
                WriteOpCode(OpCodes.Let);
            }

            inAssignTarget = true;
            Emit(node.Target);
            inAssignTarget = false;
            Emit(AddConversion(targetType, node.Value));
            return true;
        }

        public bool VisitNode(AssertStatement node)
        {
            WriteOpCode(OpCodes.Assert);
            WriteUShort((ushort)node.StartPos.Line);
            WriteByte(0);//bool debug mode - true: crash, false: log warning
            Emit(node.Condition);
            return true;
        }

        public bool VisitNode(BreakStatement node)
        {
            WriteOpCode(OpCodes.Jump);
            Nests.Peek().Add(WriteJumpPlaceholder(JumpType.Break));
            return true;
        }

        public bool VisitNode(ContinueStatement node)
        {
            Nest nest = Nests.First(n => n.Type.Has(NestType.Loop));
            if (nest.Type == NestType.ForEach)
            {
                WriteOpCode(OpCodes.IteratorNext);
                WriteOpCode(OpCodes.Jump);
                nest.Add(WriteJumpPlaceholder(JumpType.Break));
            }
            else
            {
                WriteOpCode(OpCodes.Jump);
                nest.Add(WriteJumpPlaceholder(JumpType.Continue));
            }

            return true;
        }

        public bool VisitNode(IfStatement node)
        {
            JumpPlaceholder jumpToElse = EmitConditionalJump(node.Condition);
            Emit(node.Then);
            JumpPlaceholder jumpPastElse = null;
            if (node.Else?.Statements.Any() == true)
            {
                WriteOpCode(OpCodes.Jump);
                jumpPastElse = WriteJumpPlaceholder();
                jumpToElse.End();
                Emit(node.Else);
            }
            else
            {
                jumpToElse.End();
            }
            jumpPastElse?.End();
            return true;
        }

        private JumpPlaceholder EmitConditionalJump(Expression condition)
        {
            JumpPlaceholder jump;
            if (Game is MEGame.ME3 && condition is InOpReference inOp && inOp.LeftOperand.ResolveType()?.PropertyType == EPropertyType.Object
                                                                      && inOp.LeftOperand.GetType() == typeof(SymbolReference) && inOp.RightOperand is NoneLiteral
                                                                      && (inOp.Operator.OperatorKeyword == "==" || inOp.Operator.OperatorKeyword == "!="))
            {
                SymbolReference symRef = (SymbolReference)inOp.LeftOperand;
                WriteOpCode(symRef.Node.Outer is Function ? OpCodes.OptIfLocal : OpCodes.OptIfInstance);
                WriteObjectRef(ResolveSymbol(symRef.Node));
                WriteByte((byte)(inOp.Operator.OperatorKeyword == "!=" ? 1 : 0));
                jump = WriteJumpPlaceholder(JumpType.Conditional);
            }
            else if (Game is MEGame.ME3 && condition is PreOpReference preOp && preOp.Operator.OperatorKeyword == "!"
                                                                             && preOp.Operand.GetType() == typeof(SymbolReference) && preOp.Operand.ResolveType() == SymbolTable.BoolType)
            {
                SymbolReference symRef = (SymbolReference)preOp.Operand;
                WriteOpCode(symRef.Node.Outer is Function ? OpCodes.OptIfLocal : OpCodes.OptIfInstance);
                WriteObjectRef(ResolveSymbol(symRef.Node));
                WriteByte(0);
                jump = WriteJumpPlaceholder(JumpType.Conditional);
            }
            else if (Game is MEGame.ME3 && condition is SymbolReference sr && sr.GetType() == typeof(SymbolReference) && sr.ResolveType() == SymbolTable.BoolType)
            {
                WriteOpCode(sr.Node.Outer is Function ? OpCodes.OptIfLocal : OpCodes.OptIfInstance);
                WriteObjectRef(ResolveSymbol(sr.Node));
                WriteByte(1);
                jump = WriteJumpPlaceholder(JumpType.Conditional);
            }
            else
            {
                WriteOpCode(OpCodes.JumpIfNot);
                jump = WriteJumpPlaceholder(JumpType.Conditional);
                Emit(condition);
            }

            return jump;
        }

        public bool VisitNode(ReturnStatement node)
        {
            if (Nests.Any(n => n.Type == NestType.ForEach))
            {
                WriteOpCode(OpCodes.IteratorPop);
            }
            WriteOpCode(OpCodes.Return);
            if (node.Value is null)
            {
                WriteOpCode(OpCodes.Nothing);
            }
            else
            {
                Emit(AddConversion(((Function)CompilationUnit).ReturnType, node.Value));
            }

            return true;
        }

        public bool VisitNode(StopStatement node)
        {
            WriteOpCode(OpCodes.Stop);
            return true;
        }

        public bool VisitNode(StateGoto node)
        {
            WriteOpCode(OpCodes.GotoLabel);
            Emit(node.LabelExpression);
            return true;
        }

        public bool VisitNode(Goto node)
        {
            WriteOpCode(OpCodes.Jump);
            LabelJumps.AddToListAt(node.Label, WriteJumpPlaceholder());
            return true;
        }

        public bool VisitNode(ExpressionOnlyStatement node)
        {
            
            if (GetAffector(node.Value) is Function func && func.RetValNeedsDestruction)
            {
                WriteOpCode(OpCodes.EatReturnValue);
                WriteObjectRef(ResolveReturnValue(func));
            }
            Emit(node.Value);
            return true;
        }

        public bool VisitNode(ErrorStatement node)
        {
            //an ast with errors should never be passed to the compiler
            throw new Exception($"Line {node.StartPos.Line}: Cannot compile an error!");
        }

        public bool VisitNode(ErrorExpression node)
        {
            //an ast with errors should never be passed to the compiler
            throw new Exception($"Line {node.StartPos.Line}: Cannot compile an error!");
        }

        private static Function GetAffector(Expression expr) =>
            expr switch
            {
                DelegateCall delegateCall => delegateCall.DefaultFunction,
                FunctionCall functionCall => (Function)functionCall.Function.Node,
                InOpReference inOpReference => inOpReference.Operator.Implementer,
                PreOpReference preOpReference => preOpReference.Operator.Implementer,
                CompositeSymbolRef compositeSymbolRef => GetAffector(compositeSymbolRef.InnerSymbol),
                _ => null
            };

        public bool VisitNode(DynArrayIterator node)
        {
            Emit(node.DynArrayExpression);
            Emit(node.ValueArg);
            if (node.IndexArg is null)
            {
                WriteByte(0);
                WriteOpCode(OpCodes.EmptyParmValue);
            }
            else
            {
                WriteByte(1);
                Emit(node.IndexArg);
            }

            return true;
        }

        public bool VisitNode(InOpReference node)
        {
            InOpDeclaration op = node.Operator;
            if (op.NativeIndex > 0)
            {
                WriteNativeOpCode(op.NativeIndex);
            }
            else
            {
                WriteOpCode(OpCodes.FinalFunction);
                WriteObjectRef(ResolveFunction(op.Implementer));
            }

            VariableType lType = node.Operator.LeftOperand.VarType;
            VariableType rType = node.Operator.RightOperand.VarType;
            if (node.Operator.LeftOperand.VarType is Class c && c.IsInterface)
            {
                lType = rType = node.LeftOperand.ResolveType() ?? node.RightOperand.ResolveType() ?? c;
            }

            if (op.LeftOperand.IsOut)
            {
                inAssignTarget = true;
            }
            Emit(AddConversion(lType, node.LeftOperand));
            inAssignTarget = false;
            SkipPlaceholder skip = null;
            if (op.RightOperand.Flags.Has(UnrealFlags.EPropertyFlags.SkipParm))
            {
                WriteOpCode(OpCodes.Skip);
                skip = WriteSkipPlaceholder();
            }

            Emit(AddConversion(rType, node.RightOperand));
            WriteOpCode(OpCodes.EndFunctionParms);
            skip?.End();
            return true;

        }

        public bool VisitNode(PreOpReference node)
        {
            WriteNativeOpCode(node.Operator.NativeIndex);
            Emit(node.Operand);
            WriteOpCode(OpCodes.EndFunctionParms);
            return true;
        }

        public bool VisitNode(PostOpReference node)
        {
            WriteNativeOpCode(node.Operator.NativeIndex);
            Emit(node.Operand);
            WriteOpCode(OpCodes.EndFunctionParms);
            return true;
        }

        public bool VisitNode(StructComparison node)
        {
            WriteOpCode(node.IsEqual ? OpCodes.StructCmpEq : OpCodes.StructCmpNe);
            WriteObjectRef(ResolveStruct(node.Struct));
            Emit(node.LeftOperand);
            Emit(node.RightOperand);
            return true;
        }

        public bool VisitNode(DelegateComparison node)
        {
            useInstanceDelegate = true;
            WriteOpCode(node.RightOperand.ResolveType() is DelegateType delegateType && delegateType.IsFunction
                            ? node.IsEqual
                                ? OpCodes.EqualEqual_DelFunc
                                : OpCodes.NotEqual_DelFunc
                            : node.IsEqual
                                ? OpCodes.EqualEqual_DelDel
                                : OpCodes.NotEqual_DelDel);
            Emit(node.LeftOperand);
            Emit(node.RightOperand);
            WriteOpCode(OpCodes.EndFunctionParms);
            useInstanceDelegate = false;
            return true;
        }

        public bool VisitNode(NewOperator node)
        {
            WriteOpCode(OpCodes.New);
            if (node.OuterObject is null)
            {
                WriteOpCode(OpCodes.Nothing);
            }
            else
            {
                Emit(node.OuterObject);
            }
            if (node.ObjectName is null)
            {
                WriteOpCode(OpCodes.Nothing);
            }
            else
            {
                Emit(node.ObjectName);
            }
            if (node.Flags is null)
            {
                WriteOpCode(OpCodes.Nothing);
            }
            else
            {
                Emit(node.Flags);
            }
            if (node.ObjectClass is null)
            {
                WriteOpCode(OpCodes.Nothing);
            }
            else
            {
                Emit(node.ObjectClass);
            }

            if (node.Template is null)
            {
                WriteOpCode(OpCodes.Nothing);
            }
            else
            {
                Emit(node.Template);
            }
            return true;
        }

        public bool VisitNode(FunctionCall node)
        {
            Function func = (Function)node.Function.Node;
            if (func.NativeIndex > 0)
            {
                WriteNativeOpCode(func.NativeIndex);
            }
            else if (node.Function.IsGlobal)
            {
                WriteOpCode(OpCodes.GlobalFunction);
                WriteName(func.Name);
            }
            else if (func.Flags.Has(FunctionFlags.Final) || node.Function.IsSuper)
            {
                WriteOpCode(OpCodes.FinalFunction);
                WriteObjectRef(ResolveFunction(func));
            }
            else if (Game <= MEGame.ME2 || node.IsCalledOnInterface)
            {
                WriteOpCode(OpCodes.VirtualFunction);
                WriteName(func.Name);
            }
            else
            {
                WriteOpCode(OpCodes.NamedFunction);
                WriteName(func.Name);
                if (NodeUtils.GetContainingClass(func).VirtualFunctionLookup.TryGetValue(func.Name, out ushort idx))
                {
                    WriteUShort(idx);
                }
                else
                {
                    throw new Exception($"Line {node.StartPos.Line}: Could not find '{func.Name}' in #{ContainingClass.UIndex} {ContainingClass.ObjectName}'s FullFunctions list!");
                }
            }
            CompileArguments(node.Arguments, func.Parameters);
            return true;
        }

        public bool VisitNode(DelegateCall node)
        {
            WriteOpCode(OpCodes.DelegateFunction);
            Emit(node.DelegateReference);
            WriteName(node.DefaultFunction.Name);
            CompileArguments(node.Arguments, node.DefaultFunction.Parameters);
            return true;
        }

        private void CompileArguments(List<Expression> args, List<FunctionParameter> funcParams)
        {
            for (int i = 0; i < args.Count; i++)
            {
                Expression arg = args[i];
                if (arg is null)
                {
                    WriteOpCode(OpCodes.EmptyParmValue);
                }
                else
                {
                    FunctionParameter funcParam = funcParams[i];
                    if (funcParam.IsOut)
                    {
                        inAssignTarget = true;
                    }
                    Emit(AddConversion(funcParam.VarType, arg));
                    inAssignTarget = false;
                }
            }

            WriteOpCode(OpCodes.EndFunctionParms);
        }

        public bool VisitNode(ArraySymbolRef node)
        {
            WriteOpCode(node.Array.ResolveType() is DynamicArrayType ? OpCodes.DynArrayElement : OpCodes.ArrayElement);
            Emit(node.Index);
            Emit(node.Array);
            return true;
        }

        public bool VisitNode(CompositeSymbolRef node)
        {
            bool forEachJump = inIteratorCall;
            inIteratorCall = false;
            Expression innerSymbol = node.InnerSymbol;
            if (node.IsStructMemberExpression)
            {
                if (innerSymbol.GetType() == typeof(SymbolReference) && node.Node is VariableDeclaration decl && decl.VarType == SymbolTable.BoolType)
                {
                    WriteOpCode(OpCodes.BoolVariable);
                }
                WriteOpCode(OpCodes.StructMember);
                WriteObjectRef(ResolveSymbol(node.Node));
                WriteObjectRef(ResolveStruct((Struct)node.OuterSymbol.ResolveType()));

                switch (node.OuterSymbol)
                {
                    //struct is being accessed through an rvalue
                    case FunctionCall _:
                    case DelegateCall _:
                    //case ArraySymbolRef _: doesn't seem to count as an rvalue for dynamic arrays. does it for static arrays?
                    case CompositeSymbolRef csr when ContainsFunctionCall(csr):
                    case VectorLiteral _:
                    case RotatorLiteral _:
                        WriteByte(1);
                        break;
                    default:
                        WriteByte(0);
                        break;
                }

                //this is being modified, and this is the base struct.
                if (inAssignTarget && !(node.OuterSymbol is CompositeSymbolRef csf && csf.IsStructMemberExpression))
                {
                    WriteByte(1);
                }
                else
                {
                    WriteByte(0);
                }
                Emit(node.OuterSymbol);
                return true;
            }

            //inAssignTarget = false;
            WriteOpCode(node.IsClassContext ? OpCodes.ClassContext : OpCodes.Context);
            if (node.OuterSymbol.ResolveType().PropertyType == EPropertyType.Interface)
            {
                WriteOpCode(OpCodes.InterfaceContext);
            }
            Emit(node.OuterSymbol);
            SkipPlaceholder skip = WriteSkipPlaceholder(); 
            EmitVariableSize(innerSymbol);

            skip.ResetStart();
            Emit(innerSymbol);
            if (forEachJump)
            {
                iteratorCallSkip = skip;
            }
            else
            {
                skip.End();
            }
            return true;

            static bool ContainsFunctionCall(CompositeSymbolRef csr)
            {
                while (csr != null)
                {
                    if (csr.InnerSymbol is FunctionCall || csr.InnerSymbol is DelegateCall)
                    {
                        return true;
                    }
                    csr = csr.OuterSymbol as CompositeSymbolRef;;
                }

                return false;
            }
        }

        public bool VisitNode(SymbolReference node)
        {
            switch (node.Node)
            {
                case EnumValue enumVal:
                    WriteOpCode(OpCodes.ByteConst);
                    WriteByte(enumVal.IntVal);
                    return true;
                case Function func when useInstanceDelegate:
                    WriteOpCode(OpCodes.InstanceDelegate);
                    WriteName(func.Name);
                    return true;
                case Function func:
                    WriteOpCode(OpCodes.DelegateProperty);
                    WriteName(func.Name);
                    if (Game >= MEGame.ME3)
                    {
                        WriteObjectRef(func.Flags.Has(FunctionFlags.Delegate) ? ResolveFunction(func) : null);
                    }
                    return true;
            }

            VariableDeclaration varDecl = (VariableDeclaration)node.Node;
            if (varDecl.Name == "Self")
            {
                WriteOpCode(OpCodes.Self);
                return true;
            }
            if (varDecl.VarType == SymbolTable.BoolType)
            {
                WriteOpCode(OpCodes.BoolVariable);
            }
            if (varDecl.Outer is Function)
            {
                if (varDecl.Flags.Has(UnrealFlags.EPropertyFlags.OutParm))
                {
                    WriteOpCode(OpCodes.LocalOutVariable);
                }
                else if (varDecl.IsStaticArray || Game <= MEGame.ME2)
                {
                    WriteOpCode(OpCodes.LocalVariable);
                }
                else
                {
                    switch (varDecl.VarType.PropertyType)
                    {
                        case EPropertyType.Float:
                            WriteOpCode(OpCodes.LocalFloatVariable);
                            break;
                        case EPropertyType.Int:
                            WriteOpCode(OpCodes.LocalIntVariable);
                            break;
                        case EPropertyType.Byte:
                            WriteOpCode(OpCodes.LocalByteVariable);
                            break;
                        case EPropertyType.Object:
                            WriteOpCode(OpCodes.LocalObjectVariable);
                            break;
                        default:
                            WriteOpCode(OpCodes.LocalVariable);
                            break;
                    }
                }
            }
            else
            {
                if (varDecl.IsStaticArray || Game <= MEGame.ME2)
                {
                    WriteOpCode(OpCodes.InstanceVariable);
                }
                else
                {
                    switch (varDecl.VarType.PropertyType)
                    {
                        case EPropertyType.Float:
                            WriteOpCode(OpCodes.InstanceFloatVariable);
                            break;
                        case EPropertyType.Int:
                            WriteOpCode(OpCodes.InstanceIntVariable);
                            break;
                        case EPropertyType.Byte:
                            WriteOpCode(OpCodes.InstanceByteVariable);
                            break;
                        case EPropertyType.Object:
                            WriteOpCode(OpCodes.InstanceObjectVariable);
                            break;
                        default:
                            WriteOpCode(OpCodes.InstanceVariable);
                            break;
                    }
                }
            }
            WriteObjectRef(ResolveSymbol(varDecl));
            return true;
        }

        public bool VisitNode(DefaultReference node)
        {
            if (node.Node is VariableDeclaration decl && decl.VarType == SymbolTable.BoolType)
            {
                WriteOpCode(OpCodes.BoolVariable);
            }
            WriteOpCode(OpCodes.DefaultVariable);
            WriteObjectRef(ResolveSymbol(node.Node));
            return true;
        }

        public bool VisitNode(ConditionalExpression node)
        {
            WriteOpCode(OpCodes.Conditional);
            Emit(node.Condition);
            useInstanceDelegate = true;
            using (WriteSkipPlaceholder())
            {
                Emit(node.TrueExpression);
            }

            using (WriteSkipPlaceholder())
            {
                Emit(node.FalseExpression);
            }

            useInstanceDelegate = false;

            return true;
        }

        public bool VisitNode(CastExpression node)
        {
            if (node is PrimitiveCast prim)
            {
                if (prim.Cast == ECast.ObjectToInterface)
                {
                    WriteOpCode(OpCodes.InterfaceCast);
                    WriteObjectRef(ResolveClass((Class)node.CastType));
                }
                else
                {
                    WriteOpCode(OpCodes.PrimitiveCast);
                    WriteByte((byte)prim.Cast);
                }
            }
            else if (node.CastType is ClassType clsType)
            {
                WriteOpCode(OpCodes.Metacast);
                WriteObjectRef(ResolveClass((Class)clsType.ClassLimiter));
            }
            else
            {
                WriteOpCode(OpCodes.DynamicCast);
                WriteObjectRef(ResolveClass((Class)node.CastType));
            }
            Emit(node.CastTarget);
            return true;
        }


        public bool VisitNode(DynArrayLength node)
        {
            WriteOpCode(OpCodes.DynArrayLength);
            Emit(node.DynArrayExpression);
            return true;
        }

        public bool VisitNode(DynArrayAdd node)
        {
            WriteOpCode(OpCodes.DynArrayAdd);
            Emit(node.DynArrayExpression);
            Emit(AddConversion(SymbolTable.IntType, node.CountArg));
            if (Game >= MEGame.ME3)
            {
                WriteOpCode(OpCodes.EndFunctionParms);
            }
            return true;
        }

        public bool VisitNode(DynArrayAddItem node)
        {
            WriteOpCode(OpCodes.DynArrayAddItem);
            Emit(node.DynArrayExpression);
            SkipPlaceholder placeholder = null;
            if (Game >= MEGame.ME2)
            {
                placeholder = WriteSkipPlaceholder();
            }
            DynamicArrayType dynArrType = (DynamicArrayType)node.DynArrayExpression.ResolveType();
            Emit(AddConversion(dynArrType.ElementType, node.ValueArg));
            if (Game >= MEGame.ME3)
            {
                WriteOpCode(OpCodes.EndFunctionParms);
            }
            placeholder?.End();

            return true;
        }

        public bool VisitNode(DynArrayInsert node)
        {
            WriteOpCode(OpCodes.DynArrayInsert);
            Emit(node.DynArrayExpression);
            Emit(AddConversion(SymbolTable.IntType, node.IndexArg));
            Emit(AddConversion(SymbolTable.IntType, node.CountArg));
            if (Game >= MEGame.ME3)
            {
                WriteOpCode(OpCodes.EndFunctionParms);
            }
            return true;
        }

        public bool VisitNode(DynArrayInsertItem node)
        {
            WriteOpCode(OpCodes.DynArrayInsertItem);
            Emit(node.DynArrayExpression);
            using (WriteSkipPlaceholder())
            {
                DynamicArrayType dynArrType = (DynamicArrayType)node.DynArrayExpression.ResolveType();
                Emit(AddConversion(SymbolTable.IntType, node.IndexArg));
                Emit(AddConversion(dynArrType.ElementType, node.ValueArg));
                if (Game >= MEGame.ME3)
                {
                    WriteOpCode(OpCodes.EndFunctionParms);
                }
            }
            return true;
        }

        public bool VisitNode(DynArrayRemove node)
        {
            WriteOpCode(OpCodes.DynArrayRemove);
            Emit(node.DynArrayExpression);
            Emit(AddConversion(SymbolTable.IntType, node.IndexArg));
            Emit(AddConversion(SymbolTable.IntType, node.CountArg));
            if (Game >= MEGame.ME3)
            {
                WriteOpCode(OpCodes.EndFunctionParms);
            }
            return true;
        }

        public bool VisitNode(DynArrayRemoveItem node)
        {
            WriteOpCode(OpCodes.DynArrayRemoveItem);
            Emit(node.DynArrayExpression);
            SkipPlaceholder placeholder = null;
            if (Game >= MEGame.ME2)
            {
                placeholder = WriteSkipPlaceholder();
            }
            DynamicArrayType dynArrType = (DynamicArrayType)node.DynArrayExpression.ResolveType();
            Emit(AddConversion(dynArrType.ElementType, node.ValueArg));
            if (Game >= MEGame.ME3)
            {
                WriteOpCode(OpCodes.EndFunctionParms);
            }
            placeholder?.End();
            return true;
        }

        public bool VisitNode(DynArrayFind node)
        {
            WriteOpCode(OpCodes.DynArrayFind);
            Emit(node.DynArrayExpression);
            using (WriteSkipPlaceholder())
            {
                DynamicArrayType dynArrType = (DynamicArrayType)node.DynArrayExpression.ResolveType();
                Emit(AddConversion(dynArrType.ElementType, node.ValueArg));
                if (Game >= MEGame.ME3)
                {
                    WriteOpCode(OpCodes.EndFunctionParms);
                }
            }
            return true;
        }

        public bool VisitNode(DynArrayFindStructMember node)
        {
            WriteOpCode(OpCodes.DynArrayFindStruct);
            Emit(node.DynArrayExpression);
            using (WriteSkipPlaceholder())
            {
                Emit(node.MemberNameArg);
                Emit(AddConversion(node.MemberType, node.ValueArg));
                if (Game >= MEGame.ME3)
                {
                    WriteOpCode(OpCodes.EndFunctionParms);
                }
            }
            return true;
        }

        public bool VisitNode(DynArraySort node)
        {
            WriteOpCode(OpCodes.DynArraySort);
            Emit(node.DynArrayExpression);
            using (WriteSkipPlaceholder())
            {
                Emit(node.CompareFuncArg);
                WriteOpCode(OpCodes.EndFunctionParms);
            }
            return true;
        }


        public bool VisitNode(Label node)
        {
            node.StartOffset = Position;
            return true;
        }

        public bool VisitNode(BooleanLiteral node)
        {
            WriteOpCode(node.Value ? OpCodes.True : OpCodes.False);
            return true;
        }

        public bool VisitNode(FloatLiteral node)
        {
            WriteOpCode(OpCodes.FloatConst);
            WriteFloat(node.Value);
            return true;
        }

        public bool VisitNode(IntegerLiteral node)
        {
            int i = node.Value;
            if (node.NumType != Keywords.INT && i >= 0 && i < 265)
            {
                WriteOpCode(OpCodes.ByteConst);
                WriteByte((byte)i);
            }
            else if (i == 0)
            {
                WriteOpCode(OpCodes.IntZero);
            }
            else if (i == 1)
            {
                WriteOpCode(OpCodes.IntOne);
            }
            else if (i >= 0 && i < 256)
            {
                WriteOpCode(OpCodes.IntConstByte);
                WriteByte((byte)i);
            }
            else
            {
                WriteOpCode(OpCodes.IntConst);
                WriteInt(i);
            }

            return true;
        }

        public bool VisitNode(NameLiteral node)
        {
            WriteOpCode(OpCodes.NameConst);
            WriteName(node.Value);
            return true;
        }

        public bool VisitNode(StringLiteral node)
        {
            WriteOpCode(OpCodes.StringConst);
            WriteBytes(node.Value.Select(c => (byte)c).ToArray());
            WriteByte(0);
            return true;
        }

        public bool VisitNode(StringRefLiteral node)
        {
            WriteOpCode(OpCodes.StringRefConst);
            WriteInt(node.Value);
            return true;
        }

        public bool VisitNode(ObjectLiteral node)
        {
            WriteOpCode(OpCodes.ObjectConst);
            if (node.Class is ClassType clsType)
            {
                WriteObjectRef(ResolveClass((Class)clsType.ClassLimiter));
            }
            else
            {
                IEntry entry = ResolveObject($"{ContainingClass.InstancedFullPath}.{node.Name.Value}") ?? ResolveObject(node.Name.Value);
                if (entry is null)
                {
                    throw new Exception($"Line {node.StartPos.Line}: Could not find '{node.Name.Value}' in {Pcc.FilePath}!");
                }

                if (!entry.ClassName.CaseInsensitiveEquals(node.Class.Name))
                {
                    throw new Exception($"Line {node.StartPos.Line}: Expected '{node.Name.Value}' to be a '{node.Class.Name}'!");
                }
                WriteObjectRef(entry);
            }

            return true;
        }

        public bool VisitNode(VectorLiteral node)
        {
            WriteOpCode(OpCodes.VectorConst);
            WriteFloat(node.X);
            WriteFloat(node.Y);
            WriteFloat(node.Z);
            return true;
        }

        public bool VisitNode(RotatorLiteral node)
        {
            WriteOpCode(OpCodes.RotationConst);
            WriteInt(node.Pitch);
            WriteInt(node.Yaw);
            WriteInt(node.Roll);
            return true;
        }

        public bool VisitNode(NoneLiteral node)
        {
            WriteOpCode(node.IsDelegateNone ? OpCodes.EmptyDelegate : OpCodes.NoObject);
            return true;
        }

        //TODO: remove? alreaty done in parser. doing again should have no effect
        static Expression AddConversion(VariableType destType, Expression expr)
        {

            if (expr is NoneLiteral noneLit)
            {
                if (destType.PropertyType == EPropertyType.Delegate)
                {
                    noneLit.IsDelegateNone = true;
                }
                else if (destType.PropertyType == EPropertyType.Interface)
                {
                    return new PrimitiveCast(ECast.ObjectToInterface, destType, noneLit, noneLit.StartPos, noneLit.EndPos);
                }
            }
            else if (expr?.ResolveType() is { } type && type.PropertyType != destType.PropertyType)
            {
                ECast cast = CastHelper.PureCastType(CastHelper.GetConversion(destType, type));
                switch (expr)
                {
                    case IntegerLiteral intLit:
                        switch (cast)
                        {
                            case ECast.ByteToInt:
                                intLit.NumType = Keywords.INT;
                                return intLit;
                            case ECast.IntToByte:
                                intLit.NumType = Keywords.BYTE;
                                return intLit;
                            case ECast.IntToFloat:
                                return new FloatLiteral(intLit.Value, intLit.StartPos, intLit.EndPos);
                            case ECast.ByteToFloat:
                                return new FloatLiteral(intLit.Value, intLit.StartPos, intLit.EndPos);
                        }
                        break;
                    case FloatLiteral floatLit:
                        switch (cast)
                        {
                            case ECast.FloatToByte:
                                return new IntegerLiteral((int)floatLit.Value, floatLit.StartPos, floatLit.EndPos) { NumType = Keywords.BYTE };
                            case ECast.FloatToInt:
                                return new IntegerLiteral((int)floatLit.Value, floatLit.StartPos, floatLit.EndPos) { NumType = Keywords.INT };
                        }
                        break;
                    case ConditionalExpression condExpr:
                        //TODO: create new ConditionalExpression to avoid modifying original?
                        condExpr.TrueExpression = AddConversion(destType, condExpr.TrueExpression);
                        condExpr.FalseExpression = AddConversion(destType, condExpr.FalseExpression);
                        return condExpr;
                }
                if ((byte)cast != 0 && cast != ECast.Max)
                {
                    return new PrimitiveCast(cast, destType, expr, expr.StartPos, expr.EndPos);
                }
            }

            return expr;
        }

        private IEntry ResolveSymbol(ASTNode node) =>
            node switch
            {
                Class cls => ResolveClass(cls),
                Struct strct => ResolveStruct(strct),
                State state => ResolveState(state),
                Function func => ResolveFunction(func),
                FunctionParameter param => parameters[param.Name].Export,
                VariableDeclaration local when local.Outer is Function => locals[local.Name].Export,
                VariableDeclaration field => ResolveProperty(field),
                SymbolReference symRef => ResolveSymbol(symRef.Node),
                _ => throw new ArgumentOutOfRangeException(nameof(node))
            };

        private IEntry ResolveProperty(VariableDeclaration decl)
        {
            (string name, int number) = StringToNameRef(decl.Name);
            return Pcc.getEntryOrAddImport($"{ResolveSymbol(decl.Outer).FullPath}.{name}", PropertyTypeName(decl.VarType), objIdx: number);
        }

        private IEntry ResolveStruct(Struct s) => Pcc.getEntryOrAddImport($"{ResolveSymbol(s.Outer).FullPath}.{s.Name}", "ScriptStruct");

        private IEntry ResolveFunction(Function f) => Pcc.getEntryOrAddImport($"{ResolveSymbol(f.Outer).FullPath}.{f.Name}", "Function");

        private IEntry ResolveReturnValue(Function f) => f.ReturnType is null ? null : Pcc.getEntryOrAddImport($"{ResolveFunction(f).FullPath}.ReturnValue", PropertyTypeName(f.ReturnType));

        private IEntry ResolveState(State s) => Pcc.getEntryOrAddImport($"{ResolveSymbol(s.Outer).FullPath}.{s.Name}", "State");

        private IEntry ResolveClass(Class c) =>
            EntryImporter.EnsureClassIsInFile(Pcc, c.Name, RelinkResultsAvailable: relinkResults =>
                    throw new Exception($"Unable to resolve class '{c.Name}'! There were relinker errors: {string.Join("\n\t", relinkResults.Select(pair => pair.Message))}"));

        private IEntry ResolveObject(string instancedFullPath) => Pcc.Exports.FirstOrDefault(exp => exp.InstancedFullPath == instancedFullPath) ??
                                                                  (IEntry)Pcc.Imports.FirstOrDefault(imp => imp.InstancedFullPath == instancedFullPath);

        private static string PropertyTypeName(VariableType type) =>
            type switch
            {
                Class component when component.SameAsOrSubClassOf("Component") => "ComponentProperty",
                Class _ => "ObjectProperty",
                Struct _ => "StructProperty",
                ClassType _ => "ClassProperty",
                DelegateType _ => "DelegateProperty",
                DynamicArrayType _ => "ArrayProperty",
                Enumeration _ => "ByteProperty",
                StaticArrayType staticArrayType => PropertyTypeName(staticArrayType.ElementType),
                _ when type == SymbolTable.FloatType => "FloatProperty",
                _ when type == SymbolTable.BoolType => "BoolProperty",
                _ when type == SymbolTable.BioMask4Type => "BioMask4Property",
                _ when type == SymbolTable.ByteType => "ByteProperty",
                _ when type == SymbolTable.IntType => "IntProperty",
                _ when type == SymbolTable.NameType => "NameProperty",
                _ when type == SymbolTable.StringRefType => "StringRefProperty",
                _ when type == SymbolTable.StringType => "StrProperty",
                _ => throw new ArgumentOutOfRangeException(nameof(type), type, "")
            };

        private void Emit(ASTNode node)
        {
            node.AcceptVisitor(this);
        }

        private void EmitVariableSize(Expression expr)
        {
            if (Game <= MEGame.ME2)
            {
                VariableType exprType = expr.ResolveType();
                WriteByte(exprType switch
                {
                    null => 0,
                    { PropertyType: EPropertyType.StringRef } => 0,
                    _ => (byte)exprType.Size
                });
                return;
            }

            if (GetAffector(expr) is Function f)
            {
                if (Game >= MEGame.ME3)
                {
                    WriteObjectRef(ResolveReturnValue(f));
                }

                WriteByte(0);
            }
            else if (expr is InOpReference || expr is PreOpReference || expr is PostOpReference)
            {
                if (Game >= MEGame.ME3)
                {
                    WriteObjectRef(null);
                }
                WriteByte(0);
            }
            else if (expr is PrimitiveCast p)
            {
                if (Game >= MEGame.ME3)
                {
                    WriteObjectRef(null);
                }
                WriteByte((byte)p.CastType.PropertyType);
            }
            else if (expr is SymbolReference { Node: Function })
            {
                if (Game >= MEGame.ME3)
                {
                    WriteObjectRef(null);
                }
                WriteByte((byte)EPropertyType.Delegate);
            }
            else
            {
                if (Game >= MEGame.ME3)
                {
                    WriteObjectRef(!(expr is DynArrayOperation) || expr is DynArraySort ? ResolveSymbol(expr) : null);
                }
                switch (expr)
                {
                    case DynArrayOperation dynOp when dynOp.ResolveType() == SymbolTable.IntType:
                        WriteByte((byte)EPropertyType.Int);
                        break;
                    default:
                        WriteByte(0);
                        break;
                }
            }
        }

        #region Not Bytecode

        public bool VisitNode(VariableType node)
        {
            throw new InvalidOperationException();
        }

        public bool VisitNode(StaticArrayType node)
        {
            throw new InvalidOperationException();
        }

        public bool VisitNode(DynamicArrayType node)
        {
            throw new InvalidOperationException();
        }

        public bool VisitNode(DelegateType node)
        {
            throw new InvalidOperationException();
        }

        public bool VisitNode(ClassType node)
        {
            throw new InvalidOperationException();
        }

        public bool VisitNode(EnumValue node)
        {
            throw new InvalidOperationException();
        }

        public bool VisitNode(ReturnNothingStatement node)
        {
            throw new InvalidOperationException();
        }

        public bool VisitNode(Class node)
        {
            throw new InvalidOperationException();
        }

        public bool VisitNode(Struct node)
        {
            throw new InvalidOperationException();
        }

        public bool VisitNode(Enumeration node)
        {
            throw new InvalidOperationException();
        }

        public bool VisitNode(Const node)
        {
            throw new InvalidOperationException();
        }

        public bool VisitNode(Function node)
        {
            throw new InvalidOperationException();
        }

        public bool VisitNode(State node)
        {
            throw new InvalidOperationException();
        }

        public bool VisitNode(VariableDeclaration node)
        {
            throw new InvalidOperationException();
        }

        public bool VisitNode(FunctionParameter node)
        {
            throw new InvalidOperationException();
        }

        public bool VisitNode(DefaultPropertiesBlock node)
        {
            throw new InvalidOperationException();
        }

        public bool VisitNode(Subobject node)
        {
            throw new InvalidOperationException();
        }

        public bool VisitNode(VariableIdentifier node)
        {
            throw new InvalidOperationException();
        }

        public bool VisitNode(StructLiteral node)
        {
            throw new InvalidOperationException();
        }

        public bool VisitNode(DynamicArrayLiteral node)
        {
            throw new InvalidOperationException();
        }

        #endregion
    }
}
