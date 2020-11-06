﻿using ME3Script.Analysis.Visitors;
using ME3Script.Utilities;

namespace ME3Script.Language.Tree
{
    public class VariableIdentifier : ASTNode
    {
        public string Name;
        public int Size;
        public VariableIdentifier(string name, SourcePosition start = null, SourcePosition end = null, int size = 0) 
            : base(ASTNodeType.VariableIdentifier, start, end) 
        {
            Size = size;
            Name = name;
        }

        public override bool AcceptVisitor(IASTVisitor visitor)
        {
            return visitor.VisitNode(this);
        }
    }
}
