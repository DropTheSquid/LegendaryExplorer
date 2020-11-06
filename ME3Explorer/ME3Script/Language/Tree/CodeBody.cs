﻿using ME3Script.Analysis.Visitors;
using ME3Script.Utilities;
using System.Collections.Generic;

namespace ME3Script.Language.Tree
{
    public class CodeBody : Statement
    {
        public List<Statement> Statements;

        public CodeBody(List<Statement> contents = null, SourcePosition start = null, SourcePosition end = null)
            : base(ASTNodeType.CodeBody, start, end) 
        {
            Statements = contents ?? new List<Statement>();
        }

        public override bool AcceptVisitor(IASTVisitor visitor)
        {
            return visitor.VisitNode(this);
        }
        public override IEnumerable<ASTNode> ChildNodes => Statements;
    }
}
