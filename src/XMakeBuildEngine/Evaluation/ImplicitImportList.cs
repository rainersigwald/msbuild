using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Microsoft.Build.Evaluation
{
    class ImplicitImportList
    {
        public IList<string> ImportPathFolders
        {
            get
            {
                return
                    !String.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("MSBUILDMAGICIMPORTFOLDER"))
                        ? new List<String> {Environment.GetEnvironmentVariable("MSBUILDMAGICIMPORTFOLDER")}
                        : new List<string>();
            }
        }
    }
}
