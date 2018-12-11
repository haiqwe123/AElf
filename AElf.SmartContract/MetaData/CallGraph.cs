﻿using System;
using System.Linq;
using AElf.Kernel;
using Google.Protobuf.Collections;
using QuickGraph;


namespace AElf.SmartContract.MetaData
{
    public class CallGraph : AdjacencyGraph<string, Edge<string>>, IEquatable<CallGraph>
    {
        bool IEquatable<CallGraph>.Equals(CallGraph other)
        {
            return CallingGraphToString(this) == CallingGraphToString(other);
        }
        
        private string CallingGraphToString(CallGraph callGraph)
        {
            return
                $"Edge: [{string.Join(", ", callGraph.Edges.OrderBy(a => a.Source).ThenBy(a => a.Target).Select(a => a.ToString()))}] Vertices: [{string.Join(", ", callGraph.Vertices.OrderBy(a => a))}]";
        }
    }
}