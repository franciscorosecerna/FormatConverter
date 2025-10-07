using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace FormatConverter.Interfaces
{
    public abstract class BaseOutputStrategy : IOutputFormatStrategy
    {
        protected FormatConfig Config { get; private set; } = new FormatConfig();

        public virtual void Configure(FormatConfig config)
        {
            Config = config ?? throw new ArgumentNullException(nameof(config));
        }

        public abstract string Serialize(JToken data);

        public abstract IEnumerable<string> SerializeStream(IEnumerable<JToken> data);
    }
}