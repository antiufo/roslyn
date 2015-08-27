using Microsoft.CodeAnalysis;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace CompileSimple2
{
    class JsonMetadataReference : MetadataReference
    {
        public JsonMetadataReference()
            : base(MetadataReferenceProperties.Assembly)
        {
        }

        protected JsonMetadataReference(MetadataReferenceProperties properties) : base(properties)
        {
        }

        internal override MetadataReference WithPropertiesImplReturningMetadataReference(MetadataReferenceProperties properties)
        {
            return new UnresolvedMetadataReference(this.Reference, properties);
        }

    }
}
