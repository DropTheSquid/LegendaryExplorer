﻿using ME3Script.Analysis.Visitors;
using ME3Script.Utilities;

namespace ME3Script.Language.Tree
{
    public class DefaultCaseStatement : Statement
    {
        public DefaultCaseStatement(SourcePosition start, SourcePosition end)
            : base(ASTNodeType.DefaultStatement, start, end) { }

        public override bool AcceptVisitor(IASTVisitor visitor)
        {
            return visitor.VisitNode(this);
        }
    }
}
