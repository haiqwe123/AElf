using System;
using System.Linq;
using Google.Protobuf.Collections;

namespace AElf.Kernel
{
    public partial class SerializedCallGraph
    {
        bool IEquatable<SerializedCallGraph>.Equals(SerializedCallGraph other)
        {
            var edges = new RepeatedField<GraphEdge>();
            edges.AddRange(Edges.OrderBy(e => e.Source).ThenBy(e => e.Target));
            var otherEdges = new RepeatedField<GraphEdge>();
            otherEdges.AddRange(other.Edges.OrderBy(e => e.Source).ThenBy(e => e.Target));
            
            var vertices = new RepeatedField<string>();
            vertices.AddRange(Vertices.OrderBy(v => v));
            var otherVertices = new RepeatedField<string>();
            otherVertices.AddRange(other.Vertices.OrderBy(v => v));
            
           
            return edges.Equals(otherEdges) && vertices.Equals(otherVertices);
        }
    }
}