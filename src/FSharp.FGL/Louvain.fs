﻿namespace Louvain

open FSharp.FGL
open FSharp.FGL.ArrayAdjacencyGraph
open Aether
open System.Collections.Generic

    
module Louvain =                   
    module private Dictionary = 
    
        let tryGetValue k (dict:Dictionary<'K,'V>) =
            let b,v = dict.TryGetValue(k)
            if b then Some v 
            else None
    
        let getValue' k (dict:Dictionary<'K,'V>) =
            match (tryGetValue k dict) with 
                |Some x -> x
                |None -> failwith "Error get"
        
        let getValue k (dict:Dictionary<'K,'V>) =
            try 
                dict.Item k
            with
            | _ -> failwithf "Error get k %O dict %O" k dict

        let copyRecursive (innerCopyF : 'V -> 'V) (dict:Dictionary<'K,'V>) =
            let newDict = Dictionary<'K,'V>()
            for kv in dict do
                newDict.Add(kv.Key,innerCopyF kv.Value)
            newDict
    module private Randomize =
        let rand = new System.Random()

        let swap (a: _[]) x y =
            let tmp = a.[x]
            a.[x] <- a.[y]
            a.[y] <- tmp

        // shuffle an array (in-place)
        let shuffle a =
            Array.iteri (fun i _ -> swap a i (rand.Next(i, Array.length a))) a
            
    module private GroupingFunctions =
        // Group values of an array by the groupingF and sum the values of each group after applying the valueF on each of them.
        let inline sumGroupBy (groupingF : 'T -> 'group) (valueF : 'T -> 'V) (input : ('T) []) =
        
            let length = input.Length
            let dict = System.Collections.Generic.Dictionary<'group,'V> ()
    
            // Build the groupings
            for i = 0 to length - 1 do

                let item = input.[i]
                let safeKey,v = groupingF item, valueF item
                let mutable prev = Unchecked.defaultof<'V>
                if dict.TryGetValue(safeKey, &prev) then
                    dict.[safeKey] <- prev + v
                else 
                    //dict.Add(safeKey,v)
                    dict.[safeKey] <- v
             
            // Return the array-of-sums.
            let result = Array.zeroCreate dict.Count
            let mutable i = 0
            for group in dict do
                result.[i] <- group.Key, group.Value
                i <- i + 1
            result

        //Find the summed up weights to the original community of the vertex
        let findWeightofConnectionToOldCommunity connectedCommunities originalCommunity     =   
        
            match (Array.tryFind (fun (community,weight) -> community=originalCommunity) connectedCommunities) with
                | Some x    -> (x|> snd)
                | None      -> 0.
           
    let private louvainMethod (g1:ArrayAdjacencyGraph<'Vertex,'Label,float>) (randomized:bool) : (ArrayAdjacencyGraph<'Vertex,'Label*int,float>) = 
        
        let vertices,vertices2 : Dictionary<'Vertex,'Label*int>*Dictionary<'Vertex,int*int>=
            let vertices = g1.LabelMap().Keys
            let newDictionary = System.Collections.Generic.Dictionary<'Vertex,'Label*int>()
            let newDictionary2 = System.Collections.Generic.Dictionary<'Vertex,int*int>()
            let mutable counter = 0
            for vertex in vertices do
                let newLabel = (g1.GetLabel vertex),counter
                newDictionary.Add (vertex,newLabel)
                newDictionary2.Add (vertex,(counter,counter))
                counter <- counter+1
            newDictionary,newDictionary2

        let edges =
            g1.AdjacencyGraph()

        let edges2 =
            let newEdges = System.Collections.Generic.Dictionary<int,(int*int*float)[]>()
            for v in edges do
                let key     = vertices2.Item (v.Key) |> fst
                let edges   = v.Value |> Array.map (fun (s,t,w) -> ((vertices2.Item s |> fst),(vertices2.Item s |> fst),w))
                newEdges.Add (key,edges)
            newEdges

        let verticesUpdated =
            let newVertices = System.Collections.Generic.Dictionary<int,int*int>()
            for i in vertices2 do
                let v = i.Value
                let key = fst v
                newVertices.Add (key,v)
            newVertices

        let g : (ArrayAdjacencyGraph<'Vertex,'Label*int,float>) = ArrayAdjacencyGraph(edges,vertices)
        let g2 :(ArrayAdjacencyGraph<int,int*int,float>)        = ArrayAdjacencyGraph(edges2,verticesUpdated)

        let louvainCycleInPlace (graph:ArrayAdjacencyGraph<int,int*int,float>) (randomized:bool) :(int*ArrayAdjacencyGraph<int,int*int,float>)=
        
               let verti =
                   graph.GetVertices()
               
               if randomized then
                   Randomize.shuffle verti|>ignore

               //Total weight of all edges combined
               let totalWeight =
        
                   let result = Array.zeroCreate (graph.AdjacencyGraph()).Count
                   let mutable i = 0
                   for group in (graph.AdjacencyGraph()) do
                       result.[i] <- group.Value
                       i <- i+1
                   result
                   |> Array.concat
                   |> Array.sumBy (fun (source,target,weight) -> weight)

               let neighbours =
                    [|
                        for i in verti do
                            graph.GetConnectedEdges i
                            |> Array.map(fun (s, t, w) ->
                                if s=i then (t,w)
                                else (s,w))
                        
                    |]

               let ki =
                   neighbours
                   |> Array.map(Array.sumBy snd)
                                                                              
               //All self-referencing loops of the vertices.
               let selfLoops =                                                
                   [|
                        for vertex in verti do 
                            graph.GetConnectedEdges(vertex)
                            |>Array.sumBy(fun (s,t,w) -> if s=vertex&&t=vertex then w else 0.) 
                   |]

               let communitySumtotalSumintern =
                   let output = System.Collections.Generic.Dictionary<int,float*float>() 
                   for i=0 to graph.VertexCount-1 do
                       let vertex = verti.[i]
                       let originalLabel,label = graph.GetLabel vertex
                       let communityWeightTotalStart =  ki.[i]
                       let selfLoopsStart = selfLoops.[i] 
                       output.Add(label,(communityWeightTotalStart,selfLoopsStart))
                   output       
           
               let modularityQuality startValue =
                   if startValue <> 0. then failwith "Wrong startValue"
                   let mutable q = startValue
                   for i in communitySumtotalSumintern do
                       let (totalSumC,sumIntern) = i.Value
                       if totalSumC > 0. then 
                           let calculation = (sumIntern - (totalSumC*totalSumC) / totalWeight)
                           q <- q + (calculation)
                   (q/totalWeight)

               //Minimal increase in modularity Quality that has to be achieved. If the increase in modularity is lower, then the first phase of the louvain Algorithm ends and a new iteration can begin.
               let increaseMin = 0.000001

               //Runs once over all vertices in the graph and move the vertex into the community to which the modularity gain is maximal. In case of no positive gain, the original community is kept.
               let rec louvainOneLevel (counter:int) (nbOfMoves:int) =
                   //Do until
                   if counter = graph.VertexCount then 
                       nbOfMoves > 0

                   else            
                       //Vertex that is looked at.
                       let node                                 = verti.[counter]
                   
                       //The weighted degree of the node.
                       let ki                                   = ki.[counter] //graph.WeightedDegree ((Array.sumBy(fun (s,t,w) -> w)),node)

                       let selfloopNode                         = selfLoops.[counter]
                       //Community of the node before potential improvement.
                       let (fixedCommunity,originalCommunity)   = (graph.GetLabel node)

                       //Sum of all intern weights of the originalCommunity community.
                       let (originalCommunityTotalSum,originalCommunitySumIntern)       = Dictionary.getValue originalCommunity communitySumtotalSumintern
                       //Sum of all weights that are connected to the originalCommunity.
                               
                       //Remove node from its original community.                   
                       graph.SetLabel(node,(fixedCommunity,-1)) |> ignore

                       //All neighbors of the node with their edgeWeight.         
                       let neighbors           = 
                       
                           neighbours.[counter]
                           |> Array.filter (fun (vertex,weight) -> vertex <> node) 
                   
                       //This if condition prevents problems If the node is isolated and has 0 edges. 
                       if neighbors = Array.empty then  
                           
                           graph.SetLabel(node,(fixedCommunity, originalCommunity))|> ignore
                           louvainOneLevel (counter+1) (nbOfMoves)
                   
                       else
                                      
                           //All communities the node is connected to with their edgeweight.
                           let connectedCommunities     = 
                                                  
                               neighbors
                               |> Array.map (fun (vertex,weight) -> (((graph.GetLabel vertex)|>snd),weight)) 
                           
                           //All communities the node is connected to with their edgeweight, removing duplicates. 
                           let connectedCommunitiesCondensed =
                           
                               GroupingFunctions.sumGroupBy fst snd connectedCommunities        
                           
                           //All weights to the original community of the node.
                           let weightofConnectionToOldCommunity         =   
                           
                               GroupingFunctions.findWeightofConnectionToOldCommunity connectedCommunitiesCondensed originalCommunity

                           //Removing the node from its community, updating community values communityWeightTotal and sumIntern.
                           let communityWeightTotalUpdate =  (originalCommunityTotalSum-ki)
                           let sumInternUpdate            =  (originalCommunitySumIntern-((2.*(weightofConnectionToOldCommunity))+(selfloopNode)))                  

                           communitySumtotalSumintern.Item originalCommunity <- (communityWeightTotalUpdate,sumInternUpdate)

                           let connectedCommunitiesCondensedNew =
                               Array.append [|originalCommunity,weightofConnectionToOldCommunity|] connectedCommunitiesCondensed
                               |> Array.distinct

                           //Calculating the best possible community for the node, based on modularity gain. 
                           //Outputs the bestCommunity, the gain acived by moving the node to that community and the weight of the connection to that new Community.  
                           let (bestCommunity,modularityGain,connectionToBestCommunity) =                        

                               let calculations = 
                                   connectedCommunitiesCondensedNew
                                   |> Array.map (fun (community,connectionToCommunity) -> 
                                           (
                                           community,
                                           (connectionToCommunity-((Dictionary.getValue community communitySumtotalSumintern|>fst)*ki/totalWeight)),
                                           connectionToCommunity
                                           )
                                       )

                               calculations
                               |> Array.maxBy (fun (community,modularityGain,connectionToCommunity) -> modularityGain)
                                                
                           if modularityGain < 0.  then 
                           
                               //Resetting the community to its original state.                       
                               graph.SetLabel (node,(fixedCommunity,originalCommunity)) |> ignore
                               communitySumtotalSumintern.Item originalCommunity <- (originalCommunityTotalSum,originalCommunitySumIntern)
                       
                               louvainOneLevel (counter+1) (nbOfMoves)

                           else                                           
                               let (communityNewSum,communityNewIn) = Dictionary.getValue bestCommunity communitySumtotalSumintern

                               //Moving the node to its new community.
                               let sumInternBestCommunity              =      (communityNewIn+((2.*(connectionToBestCommunity)+(selfloopNode))))
                               let communityWeightTotalBestCommunity   =      (communityNewSum+ki)
                           
                               graph.SetLabel (node,(fixedCommunity,bestCommunity)) |> ignore
                               communitySumtotalSumintern.Item bestCommunity <- (communityWeightTotalBestCommunity,sumInternBestCommunity)

                               (if bestCommunity <> originalCommunity then (nbOfMoves+1) else nbOfMoves)
                               |> louvainOneLevel (counter+1) 
         
               //A loop that executes louvainOneLevel as long as none of the exit conditions are met.
               //The exit conditions are
               // 1) No improvement was preformed 
               // 2) The increase in modularityQuality by preforming the louvainOneLevel results in a score lower than the increaseMin.
               let rec loop nbOfMoves currentQuality improvement :(int*ArrayAdjacencyGraph<int,int*int,float>)=
                   let build (shouldIBuild:bool) =
                       if not shouldIBuild then
                           failwith "ERROR"
                       else
                       
                           let (vertexToLabelMap,vertexNewLabel) :((Map<int,int>)*(Dictionary<int,int>))=
                               let labelMap =
                                   graph.GetLabels()
                                   |> Array.map snd
                                   |> Array.distinct
                                   |> Array.mapi (fun i label -> (label,i))
                                   |> Map.ofArray
                               let labelMap2 = 
                                   [|
                                        for (oldCommunity,newCommunity) in graph.GetLabels() do
                                            oldCommunity,labelMap.[newCommunity]
                                   |]
                                   |> Map.ofArray

                               let vertexDict = System.Collections.Generic.Dictionary<int,int>()
                               for i in verti do
                                   vertexDict.Add (i,(labelMap.[(graph.GetLabel i)|>snd]))

                               labelMap2,vertexDict                         

                           for i in g.GetVertices() do
                               let (originalLabel,currentLabel) = g.GetLabel(i)
                               let updateLabel     = vertexToLabelMap.[currentLabel]
                               g.SetLabel(i,(originalLabel,updateLabel))
                               |> ignore
                      
                           let vert = 
                                vertexToLabelMap
                                |> Map.toArray
                                |> Array.map snd
                                |> Array.distinct
                                |> Array.map (fun x -> (x,(x,x)))
                                |> Array.toList

                           let edgeListUpdated :(int*int*float)[]=

                               let getLabel vertex =
                                   Dictionary.getValue vertex vertexNewLabel
   
                               let edgesToLabelEdges :(int*int*float)[] = 
                                   //graph.GetEdges()
                                   let result = Array.zeroCreate (graph.AdjacencyGraph()).Count
                                   let mutable i = 0
                                   for group in (graph.AdjacencyGraph()) do
                                       result.[i] <- group.Value
                                       i <- i+1
                                   result
                                   |> Array.concat
                                   |> Array.map (fun (s,t,w) -> ((getLabel s),(getLabel t),w))

                               let output = System.Collections.Generic.Dictionary<int*int,float>()
                               for (s,t,w) in edgesToLabelEdges do
                                   if output.ContainsKey (s,t) then 
                                       let value = Dictionary.getValue ((s,t)) output
                                       if s=t then 
                                           output.Item ((s,t)) <- (value+w)
                                       else
                                           output.Item ((s,t)) <- (value+(w/2.))
    
                                   elif output.ContainsKey (t,s) then
                                       let value = Dictionary.getValue ((t,s)) output
                                       if s=t then 
                                           output.Item ((t,s)) <- (value+w)
                                       else
                                           output.Item ((t,s)) <- (value+(w/2.))
    
                                   else
                                       if s=t then 
                                           output.Add ((s,t),w)
                                       else
                                           output.Add ((s,t),(w/2.))

                               let result = Array.zeroCreate output.Count
                               let mutable i = 0
                               for group in output do
                                   let (s,t)   = group.Key
                                   let (w)     = group.Value
                                   result.[i] <- (s,t,w)
                                   i <- i + 1
                               result
                           
                           nbOfMoves,                                    
                           ArrayAdjacencyGraph(
                               (vert),
                               (edgeListUpdated |> Array.toList)
                           )

                   let qualityNew = modularityQuality 0.
                   if nbOfMoves = 0 then 
                 
                       let hasImProved = louvainOneLevel 0 0

                       loop (nbOfMoves+1) currentQuality hasImProved
           
                   elif improvement then 
                      
                       if (qualityNew-currentQuality) > increaseMin then 
                         
                               loop (nbOfMoves+1) (qualityNew) (louvainOneLevel 0 0)

                       else                    
                            build true
                   elif improvement = false && nbOfMoves = 1 then 
                                  
                        nbOfMoves,
                        graph               
                        //build true

                   else 
                        build true
                    
               //Start the louvainApplication
               loop 0 (modularityQuality 0.) false

        //The louvainLoop combines the two phases of the louvain Algorithm. As long as improvments can be performed, the louvainApplication is executed.
        let rec louvainInPlace_ nbOfLoops (newG:ArrayAdjacencyGraph<int,int*int,float>) =
        
            let (nbOfMoves,newGraph) = 
            
                louvainCycleInPlace newG randomized           

            if nbOfMoves < 2 then 
            
                g

            else 

                louvainInPlace_ (nbOfLoops+1) newGraph


        louvainInPlace_ 0 g2
    
    let louvain (graph:ArrayAdjacencyGraph<'Vertex,'Label,float>) :(ArrayAdjacencyGraph<'Vertex,'Label*int,float>)=
        louvainMethod graph false

    let louvainRandom (graph:ArrayAdjacencyGraph<'Vertex,'Label,float>) :(ArrayAdjacencyGraph<'Vertex,'Label*int,float>)=
        louvainMethod graph true
