﻿using System;
using System.Collections.Generic;

namespace ME3Script.Lexing.Tokenizing
{
    public class TokenizableDataStream<D> where D : class
    {
        #region Members
        protected readonly List<D> Data;
        private readonly Stack<int> Snapshots;
        public int CurrentIndex { get; protected set; }

        public virtual D CurrentItem => EndOfStream() ? null : Data[CurrentIndex];

        #endregion

        #region Public Methods
        public TokenizableDataStream(Func<List<D>> provider)
        {
            CurrentIndex = 0;
            Snapshots = new Stack<int>();
            Data = provider();
        }

        public void PushSnapshot()
        {
            Snapshots.Push(CurrentIndex);
        }

        public void DiscardSnapshot()
        {
            Snapshots.Pop();
        }

        public void PopSnapshot()
        {
            CurrentIndex = Snapshots.Pop();
        }

        public virtual D LookAhead(int reach)
        {
            return EndOfStream(reach) ? null : Data[CurrentIndex + reach];
        }

        public virtual D Prev()
        {
            return CurrentIndex <= 0 ? null : Data[CurrentIndex - 1];
        }

        public void Advance(int num = 1)
        {
            CurrentIndex += num;
        }

        public bool AtEnd()
        {
            return EndOfStream();
        }
        #endregion

        #region Private Methods
        private bool EndOfStream(int ahead = 0)
        {
            return CurrentIndex + ahead >= Data.Count;
        }
        #endregion
    }
}
