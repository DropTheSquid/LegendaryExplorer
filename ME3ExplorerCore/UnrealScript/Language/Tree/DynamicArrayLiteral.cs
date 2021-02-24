﻿using System.Collections.Generic;
using ME3Script.Analysis.Visitors;
using ME3Script.Utilities;

namespace ME3Script.Language.Tree
{
    public class DynamicArrayLiteral : Expression
    {
        public List<Expression> Values;

        public string ElementType;
        public DynamicArrayLiteral(string elementType, List<Expression> values, SourcePosition start = null, SourcePosition end = null) : base(ASTNodeType.DynamicArrayLiteral, start, end)
        {
            ElementType = elementType;
            Values = values;
        }

        public override bool AcceptVisitor(IASTVisitor visitor)
        {
            return visitor.VisitNode(this);
        }

        public override VariableType ResolveType()
        {
            return new VariableType(ElementType, null, null);
        }
        public override IEnumerable<ASTNode> ChildNodes => Values;
    }
}
