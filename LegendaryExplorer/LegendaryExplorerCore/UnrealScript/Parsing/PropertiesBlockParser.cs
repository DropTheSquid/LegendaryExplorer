﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using LegendaryExplorerCore.Helpers;
using LegendaryExplorerCore.Packages;
using LegendaryExplorerCore.UnrealScript.Analysis.Symbols;
using LegendaryExplorerCore.UnrealScript.Analysis.Visitors;
using LegendaryExplorerCore.UnrealScript.Compiling.Errors;
using LegendaryExplorerCore.UnrealScript.Language.Tree;
using LegendaryExplorerCore.UnrealScript.Language.Util;
using LegendaryExplorerCore.UnrealScript.Lexing.Tokenizing;
using LegendaryExplorerCore.UnrealScript.Utilities;
using static LegendaryExplorerCore.UnrealScript.Utilities.Keywords;

namespace LegendaryExplorerCore.UnrealScript.Parsing
{
    //WIP
    public class PropertiesBlockParser : StringParserBase
    {
        private readonly Stack<string> ExpressionScopes;
        private readonly IMEPackage Pcc;
        private bool IsStructDefaults;

        public static void Parse(DefaultPropertiesBlock propsBlock, bool isStructDefaults, IMEPackage pcc, SymbolTable symbols, MessageLog log)
        {
            var parser = new PropertiesBlockParser(propsBlock, isStructDefaults, pcc, symbols, log);
            var statements = parser.Parse(false);

            propsBlock.Statements = statements;
        }

        private PropertiesBlockParser(DefaultPropertiesBlock propsBlock, bool isStructDefaults, IMEPackage pcc, SymbolTable symbols, MessageLog log)
        {
            Symbols = symbols;
            Log = log;
            Tokens = propsBlock.Tokens;
            Pcc = pcc;
            IsStructDefaults = isStructDefaults;

            ExpressionScopes = new Stack<string>();
            ExpressionScopes.Push(Symbols.CurrentScopeName);
        }

        private List<Statement> Parse(bool requireBrackets = true)
        {
            if (requireBrackets && Consume(TokenType.LeftBracket) == null) throw ParseError("Expected '{'!", CurrentPosition);

            var statements = new List<Statement>();
            try
            {
                Symbols.PushScope("DefaultProperties");
                var current = ParseTopLevelStatement();
                while (current != null)
                {
                    statements.Add(current);
                    current = ParseTopLevelStatement();
                }
            }
            finally
            {
                Symbols.PopScope();
            }

            if (requireBrackets && Consume(TokenType.RightBracket) == null) throw ParseError("Expected '}'!", CurrentPosition);
            return statements;
        }

        private Statement ParseTopLevelStatement()
        {
            if (CurrentIs("BEGIN") && NextIs("Object"))
            {
                if (IsStructDefaults)
                {
                    throw ParseError($"SubObjects are not allowed in {STRUCTDEFAULTPROPERTIES}!", CurrentPosition);
                }
                return ParseSubobject();
            }

            return ParseNonStructAssignment();
        }

        private Subobject ParseSubobject()
        {
            var startPos = CurrentPosition;
            Tokens.Advance(2);// Begin Object

            if (!Matches("Class") || !Matches(TokenType.Assign))
            {
                throw ParseError("Expected 'Class=' after 'Begin Object'!", CurrentPosition);
            }

            var classNameToken = Consume(TokenType.Word);
            if (classNameToken is null)
            {
                throw ParseError("Expected name of class!", CurrentPosition);
            }
            

            if (!Symbols.TryGetType(classNameToken.Value, out Class objectClass))
            {
                throw ParseError($"{classNameToken} is not the name of a class!", classNameToken);
            }


            if (!Matches("Name") || !Matches(TokenType.Assign))
            {
                throw ParseError("Expected 'Name=' after Class reference!", CurrentPosition);
            }

            var nameToken = Consume(TokenType.Word);
            if (nameToken is null)
            {
                throw ParseError("Expected name of Object!", CurrentPosition);
            }

            var statements = new List<Statement>();

            ExpressionScopes.Push(objectClass.GetInheritanceString());
            try
            {
                while (true)
                {
                    Statement current;
                    if (CurrentIs("BEGIN") && NextIs("Object"))
                    {
                        current = ParseSubobject();
                    }
                    else if (CurrentIs("END") && NextIs("Object"))
                    {
                        Tokens.Advance(2);// END Object
                        var subObj = new Subobject(new VariableDeclaration(objectClass, default, nameToken.Value), objectClass, statements, startPos, PrevToken.EndPos);
                        if (!Symbols.TryAddSymbol(nameToken.Value, subObj))
                        {
                            throw ParseError($"'{nameToken.Value}' has already been defined in this scope!");
                        }
                        return subObj;
                    }
                    else
                    {
                        current = ParseNonStructAssignment();
                    }

                    if (current is null)
                    {
                        throw ParseError("Subobject declarations must be closed with 'End Object' !");
                    }
                    statements.Add(current);
                }
            }
            finally
            {
                ExpressionScopes.Pop();
            }
        }

        private AssignStatement ParseNonStructAssignment()
        {
            if (CurrentIs(TokenType.RightBracket))
            {
                return null;
            }
            var statement = ParseAssignment(false);
            if (statement is null)
            {
                return null;
            }

            Consume(TokenType.SemiColon); //semicolon's are optional
            return statement;
        }

        private AssignStatement ParseAssignment(bool inStruct)
        {
            if (Consume(TokenType.Word) is Token<string> propName)
            {
                var target = ParsePropName(propName, inStruct);
                VariableType targetType = target.ResolveType();
                if (Matches(TokenType.LeftSqrBracket))
                {
                    Expression expression = ParseLiteral();
                    if (expression is not IntegerLiteral intLit)
                    {
                        throw ParseError("Expected an integer index!", expression?.StartPos ?? CurrentPosition, expression?.EndPos);
                    }
                    
                    if (targetType is not StaticArrayType arrType)
                    {
                        TypeError("Cannot index a property that is not a static array!", intLit);
                    }
                    else if (intLit.Value >= arrType.Length)
                    {
                        TypeError($"'{propName}' only has {arrType.Length} elements!", intLit);
                    }
                    else if (intLit.Value < 0)
                    {
                        TypeError("Index cannot be a negative number!", intLit);
                    }
                    else
                    {
                        targetType = arrType.ElementType;
                    }

                    if (Consume(TokenType.RightSqrBracket) is not {} closeBracket)
                    {
                        throw ParseError("Expected a ']'!", CurrentPosition);
                    }
                    target = new ArraySymbolRef(target, intLit, target.StartPos, closeBracket.EndPos);
                }
                else if (targetType is StaticArrayType)
                {
                    throw ParseError($"Cannot assign directly to a static array! You must assign to each index individually, (eg. {propName.Value}[0] = ...)", propName);
                }
                if (Matches(TokenType.Assign, EF.Operator))
                {
                    Expression literal = ParseValue(targetType);
                    return new AssignStatement(target, literal, propName.StartPos, literal.EndPos);
                }

                throw ParseError("Expected '=' in assignment statement!", CurrentPosition);
            }

            throw ParseError("Expected name of property!", CurrentPosition);
        }

        private Expression ParseValue(VariableType targetType)
        {
            Expression literal;
            if (Matches(TokenType.LeftBracket))
            {
                if (targetType is not Struct targetStruct)
                {
                    throw ParseError($"A '{{' is used to start a struct. Expected a {targetType.FullTypeName()} literal!", CurrentPosition);
                }
                literal = FinishStructLiteral(targetStruct);
            }
            else if (Matches(TokenType.LeftParenth))
            {
                switch (targetType)
                {
                    case DynamicArrayType dynamicArrayType:
                        literal = FinishDynamicArrayLiteral(dynamicArrayType);
                        break;
                    case Struct:
                        ParseError("Use '{' for struct literals, not '('.", CurrentPosition);
                        goto default;
                    default:
                        throw ParseError($"A '(' is used to start a dynamic array literal. Expected a {targetType.FullTypeName()} literal!", CurrentPosition);
                }
            }
            else
            {
                bool isNegative = Matches(TokenType.MinusSign, EF.Operator);

                literal = ParseLiteral();
                if (literal is not null)
                {
                    if (isNegative)
                    {
                        switch (literal)
                        {
                            case FloatLiteral floatLiteral:
                                floatLiteral.Value *= -1;
                                break;
                            case IntegerLiteral integerLiteral:
                                integerLiteral.Value *= -1;
                                break;
                            default:
                                throw ParseError("Malformed constant value!", CurrentPosition);
                        }
                    }
                }
                else
                {
                    if (isNegative)
                    {
                        throw ParseError("Unexpected '-' !", CurrentPosition);
                    }

                    if (Consume(TokenType.Word) is { } token)
                    {
                        if (Consume(TokenType.NameLiteral) is { } objName)
                        {
                            literal = ParseObjectLiteral(token, objName);
                        }
                        else
                        {
                            literal = ParseBasicRef(token, targetType is DelegateType);
                            if (literal is SymbolReference {Node: Const cnst})
                            {
                                literal = cnst.Literal;
                            }
                        }
                    }
                    else
                    {
                        throw ParseError("Expected a value!", CurrentPosition);
                    }
                }
            }

            VerifyLiteral(targetType, ref literal);
            return literal;
        }

        private StructLiteral FinishStructLiteral(Struct targetStruct)
        {
            Token<string> openingBracket = PrevToken;
            var statements = new List<Statement>();

            if (!Matches(TokenType.RightBracket))
            {
                ExpressionScopes.Push(targetStruct.GetScope());
                try
                {
                    var statement = ParseAssignment(true);
                    statements.Add(statement);
                    while (Matches(TokenType.Comma))
                    {
                        statement = ParseAssignment(true);
                        statements.Add(statement);
                    }
                    if (!Matches(TokenType.RightBracket))
                    {
                        throw ParseError("Expected struct literal to end with a '}'!", openingBracket.StartPos, CurrentPosition);
                    }
                }
                finally
                {
                    ExpressionScopes.Pop();
                }
            }

            return new StructLiteral(targetStruct, statements, PrevToken.StartPos, PrevToken.EndPos);
        }

        private DynamicArrayLiteral FinishDynamicArrayLiteral(DynamicArrayType arrayType)
        {
            Token<string> openingParen = PrevToken;
            var values = new List<Expression>();

            if (!Matches(TokenType.RightParenth))
            {
                var targetType = arrayType.ElementType;
                var value = ParseValue(targetType);
                values.Add(value);
                while (Matches(TokenType.Comma))
                {
                    value = ParseValue(targetType);
                    values.Add(value);
                }

                if (!Matches(TokenType.RightParenth))
                {
                    throw ParseError("Expected array literal to end with a ')'!", openingParen.StartPos, CurrentPosition);
                }
            }

            return new DynamicArrayLiteral(arrayType, values, openingParen.StartPos, PrevToken.EndPos);
        }

        private void VerifyLiteral(VariableType targetType, ref Expression literal)
        {
            switch (targetType)
            {
                case Class targetClass:
                    if (literal is not NoneLiteral)
                    {
                        VariableType valueClass;
                        if (literal is ObjectLiteral objectLiteral)
                        {
                            valueClass = objectLiteral.Class;
                        }
                        else if (literal is SymbolReference {Node: Subobject {Class: Class subObjClass}})
                        {
                            valueClass = subObjClass;
                        }
                        else
                        {
                            TypeError($"Expected an {OBJECT} literal or sub-object name!", literal);
                            break;
                        }

                        if (valueClass is not (Class or ClassType)
                            || valueClass is Class literalClass && !literalClass.SameAsOrSubClassOf(targetClass.Name)
                            || valueClass is ClassType && targetClass.Name is not ("Class" or "Object"))
                        {
                            TypeError($"Expected an object of class {targetClass.Name} or a subclass!", literal);
                        }
                    }

                    break;
                case ClassType targetClassLimiter:
                    if (literal is not NoneLiteral)
                    {
                        if (literal is not ObjectLiteral {Class: ClassType literalClassType})
                        {
                            TypeError($"Expected a class literal!", literal);
                        }
                        else if (targetClassLimiter.ClassLimiter != literalClassType.ClassLimiter && !((Class)literalClassType.ClassLimiter).SameAsOrSubClassOf(targetClassLimiter.ClassLimiter.Name))
                        {
                            TypeError($"Cannot assign a value of type '{literalClassType.FullTypeName()}' to a variable of type '{targetClassLimiter.FullTypeName()}'.", literal);
                        }
                    }

                    break;
                case DelegateType delegateType:
                    if (literal is not SymbolReference {Node: Function func})
                    {
                        TypeError("Expected a function reference!", literal);
                    }
                    else if (!func.SignatureEquals(delegateType.DefaultFunction))
                    {
                        TypeError($"Expected a function with the same signature as {(delegateType.DefaultFunction.Outer as Class)?.Name}.{delegateType.DefaultFunction.Name}!", literal);
                    }

                    break;
                case DynamicArrayType:
                    if (literal is not DynamicArrayLiteral)
                    {
                        TypeError($"Expected a dynamic array literal!", literal);
                    }
                    break;
                case Enumeration enumeration:
                    if (literal is not NoneLiteral)
                    {
                        if (literal is not SymbolReference {Node: EnumValue enumVal})
                        {
                            TypeError($"Expected an enum value!", literal);
                        }
                        else if (enumeration != enumVal.Enum)
                        {
                            TypeError($"Expected an {enumeration.Name} value, not an {enumVal.Enum.Name} value!", literal);
                        }
                    }
                    break;
                case Struct:
                    if (literal is not StructLiteral)
                    {
                        TypeError($"Expected a {STRUCT} literal!", literal);
                    }
                    break;
                default:
                    switch (targetType.PropertyType)
                    {
                        case EPropertyType.Byte:
                            if (literal is not IntegerLiteral byteLiteral)
                            {
                                TypeError($"Expected a {BYTE}!", literal);
                            }
                            else if (byteLiteral.Value is < 0 or > 255)
                            {
                                TypeError($"{byteLiteral.Value} is not in the range of valid byte values: [0, 255]", literal);
                            }

                            break;
                        case EPropertyType.Int:
                            if (literal is not IntegerLiteral)
                            {
                                TypeError($"Expected an integer!", literal);
                            }

                            break;
                        case EPropertyType.Bool:
                            if (literal is not BooleanLiteral)
                            {
                                TypeError($"Expected {TRUE} or {FALSE}!");
                            }

                            break;
                        case EPropertyType.Float:
                            if (literal is IntegerLiteral intLit)
                            {
                                literal = new FloatLiteral(intLit.Value, intLit.StartPos, intLit.EndPos);
                            }
                            else if (literal is not FloatLiteral)
                            {
                                TypeError($"Expected a floating point number!", literal);
                            }

                            break;
                        case EPropertyType.Name:
                            if (literal is not NameLiteral)
                            {
                                TypeError($"Expected a {NAME} literal!", literal);
                            }

                            break;
                        case EPropertyType.String:
                            if (literal is not StringLiteral)
                            {
                                TypeError($"Expected a {STRING} literal!", literal);
                            }

                            break;
                        case EPropertyType.StringRef:
                            if (literal is not StringRefLiteral)
                            {
                                TypeError($"Expected a {STRINGREF} literal!", literal);
                            }

                            break;
                        default:
                            throw new ArgumentOutOfRangeException();
                    }

                    break;
            }
        }

        private SymbolReference ParsePropName(Token<string> token, bool inStruct)
        {
            string specificScope = ExpressionScopes.Peek();
            if (!Symbols.TryGetSymbolInScopeStack(token.Value, out ASTNode symbol, specificScope) 
                || inStruct && symbol.Outer is not Struct)
            {
                //TODO: better error message
                TypeError($"{specificScope} has no member named '{token.Value}'!", token);
                symbol = new VariableType("ERROR");
            }

            return NewSymbolReference(symbol, token, false);
        }

        private SymbolReference ParseBasicRef(Token<string> token, bool useExpressionScope)
        {
            string specificScope = useExpressionScope ? ExpressionScopes.Peek() : Symbols.CurrentScopeName;
            if (!Symbols.TryGetSymbolInScopeStack(token.Value, out ASTNode symbol, specificScope))
            {
                //const, or enum
                if (Symbols.TryGetType(token.Value, out VariableType destType))
                {
                    token.AssociatedNode = destType;
                    if (destType is Enumeration enm && Matches(TokenType.Dot))
                    {
                        token.SyntaxType = EF.Enum;
                        if (Consume(TokenType.Word) is { } enumValName
                         && enm.Values.FirstOrDefault(val => val.Name.CaseInsensitiveEquals(enumValName.Value)) is EnumValue enumValue)
                        {
                            enumValName.AssociatedNode = enm;
                            return NewSymbolReference(enumValue, enumValName, false);
                        }
                        throw ParseError("Expected valid enum value!", CurrentPosition);
                    }
                    if (destType is Const cnst)
                    {
                        return NewSymbolReference(cnst, token, false);
                    }
                }
                //TODO: better error message
                TypeError($"{specificScope} has no member named '{token.Value}'!", token);
                symbol = new VariableType("ERROR");
            }
            
            return NewSymbolReference(symbol, token, false);
        }
    }
}
