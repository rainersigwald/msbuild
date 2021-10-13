// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
//

using System;

#nullable enable

namespace Microsoft.Build.Framework
{
    [Serializable]
    public class TaskProgressEventArgs : BuildEventArgs
    {
        private int _completed;
        private int _total;

        public int Completed => _completed;
        public int Total => _total;

        /// <summary>
        /// 
        /// </summary>
        /// <param name="completed"></param>
        /// <param name="total"></param>
        public TaskProgressEventArgs(int completed, int total)
            : base()
        {
            _completed = completed;
            _total = total;
        }
    }
}
