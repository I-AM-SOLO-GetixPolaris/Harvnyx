using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Harvnyx
{
    // 
    [AttributeUsage(AttributeTargets.Assembly, AllowMultiple = false)]
    public class PluginModuleAttribute : Attribute
    {
        public string Name { get; }
        public string Version { get; }
        public PluginModuleAttribute(string name, string version)
        {
            Name = name;
            Version = version;
        }
    }
}
