﻿(**********************************************************************************)
(* FIO - A type-safe, highly concurrent programming library for F#                *)
(* Copyright (c) 2025, Daniel Larsen and Technical University of Denmark (DTU)    *)
(* All rights reserved                                                            *)
(**********************************************************************************)

module FIO.Monitor

open FIO.Core

open System.Collections.Concurrent

[<AbstractClass>]
type internal Worker() =
    abstract Working : unit -> bool

type internal DeadlockDetector<'B, 'E when 'B :> Worker and 'E :> Worker>(
    workItemQueue: BlockingCollection<WorkItem>,
    intervalMs: int) as self =
    let blockingItems = new ConcurrentDictionary<BlockingItem, Unit>()
    let mutable blockingWorkers : List<'B> = []
    let mutable evalWorkers : List<'E> = []
    let mutable countDown = 10
    let _ = (async {
        while true do
            (*
                * If there's no work left in the work queue and no eval workers are working,
                * BUT there are still blocking items, then we know we have a deadlock.
                *)
            if workItemQueue.Count <= 0 
                && self.AllEvalWorkersIdle()
                && blockingItems.Count > 0
            then
                if countDown <= 0 then
                    printfn "DEADLOCK_DETECTOR: ############ WARNING: Potential deadlock detected! ############"
                    printfn "DEADLOCK_DETECTOR:     Suspicion: No work items left, All EvalWorkers idling, Existing blocking items"
                else
                    countDown <- countDown - 1
            else
                countDown <- 10
            System.Threading.Thread.Sleep(intervalMs)
    } |> Async.StartAsTask |> ignore)
                    
    member internal _.AddBlockingItem blockingItem =
        blockingItems.TryAdd (blockingItem, ())
        |> ignore
                
    member internal _.RemoveBlockingItem (blockingItem: BlockingItem) =
        blockingItems.TryRemove blockingItem
        |> ignore

    member private _.AllEvalWorkersIdle() =
        not (List.contains true <| 
            List.map (fun (evalWorker: 'E) ->
                evalWorker.Working()) evalWorkers)

    member private _.AllBlockingWorkersIdle() =
        not (List.contains true <| 
            List.map (fun (evalWorker: 'B) ->
                evalWorker.Working()) blockingWorkers)

    member internal _.SetEvalWorkers workers =
        evalWorkers <- workers

    member internal _.SetBlockingWorkers workers =
        blockingWorkers <- workers

type internal Monitor(
    workItemQueue: BlockingCollection<WorkItem>,
    blockingItemQueue: Option<BlockingCollection<BlockingItem * WorkItem>>,
    blockingEventQueue: Option<BlockingCollection<Channel<obj>>>,
    blockingWorkItemMap: Option<ConcurrentDictionary<BlockingItem, BlockingCollection<WorkItem>>>) as self =
    let _ = (async {
        while true do
            printfn "\n\n"
            self.PrintWorkItemQueueInfo workItemQueue
            printfn "\n"
            match blockingItemQueue with
            | Some queue -> self.PrintBlockingItemQueueInfo queue
                            printfn "\n"
            | _ -> ()
            match blockingEventQueue with
            | Some queue -> self.PrintBlockingEventQueueInfo queue
                            printfn "\n"
            | _ -> ()
            match blockingWorkItemMap with
            | Some map -> self.PrintBlockingWorkItemMapInfo map
                          printfn "\n"
            | _ -> ()
            System.Threading.Thread.Sleep(1000)
    } |> Async.StartAsTask |> ignore)

    member private _.PrintWorkItemQueueInfo (queue : BlockingCollection<WorkItem>) =
        printfn $"MONITOR: workItemQueue count: %i{queue.Count}"
        printfn "MONITOR: ------------ workItemQueue information start ------------"
        for workItem in queue.ToArray() do
            let ifiber = workItem.IFiber
            printfn $"MONITOR:    ------------ workItem start ------------"
            printfn $"MONITOR:      WorkItem IFiber completed: %A{ifiber.Completed()}"
            printfn $"MONITOR:      WorkItem IFiber blocking items count: %A{ifiber.BlockingWorkItemsCount()}"
            printfn $"MONITOR:      WorkItem PrevAction: %A{workItem.PrevAction}"
            printfn $"MONITOR:      WorkItem Eff: %A{workItem.Eff}"
            printfn $"MONITOR:    ------------ workItem end ------------"
        printfn "MONITOR: ------------ workItemQueue information end ------------"

    member private _.PrintBlockingItemQueueInfo (queue : BlockingCollection<BlockingItem * WorkItem>) =
        printfn $"MONITOR: blockingItemQueue count: %i{queue.Count}"
        printfn "MONITOR: ------------ blockingItemQueue information start ------------"
        for blockingItem, workItem in queue.ToArray() do
            printfn $"MONITOR:    ------------ BlockingItem * WorkItem start ------------"
            match blockingItem with
            | BlockingChannel chan ->
                printfn $"MONITOR:      Blocking Channel count: %A{chan.Count}"
            | BlockingFiber ifiber ->
                printfn $"MONITOR:      Blocking IFiber completed: %A{ifiber.Completed()}"
                printfn $"MONITOR:      Blocking IFiber blocking items count: %A{ifiber.BlockingWorkItemsCount()}"
            let ifiber = workItem.IFiber
            printfn $"MONITOR:      WorkItem IFiber completed: %A{ifiber.Completed()}"
            printfn $"MONITOR:      WorkItem IFiber blocking items count: %A{ifiber.BlockingWorkItemsCount()}"
            printfn $"MONITOR:      WorkItem PrevAction: %A{workItem.PrevAction}"
            printfn $"MONITOR:      WorkItem Eff: %A{workItem.Eff}"
            printfn $"MONITOR:    ------------ BlockingItem * WorkItem end ------------"
        printfn "MONITOR: ------------ workItemQueue information end ------------"
               
    member private _.PrintBlockingEventQueueInfo (queue : BlockingCollection<Channel<obj>>) =
        printfn $"MONITOR: blockingEventQueue count: %i{queue.Count}"
        printfn "MONITOR: ------------ blockingEventQueue information start ------------"
        for blockingChan in queue.ToArray() do
            printfn $"MONITOR:    ------------ blockingChan start ------------"
            printfn $"MONITOR:      Count: %A{blockingChan.Count()}"
            printfn $"MONITOR:    ------------ blockingChan end ------------"
        printfn "MONITOR: ------------ blockingEventQueue information end ------------"

    member private _.PrintBlockingWorkItemMapInfo (map : ConcurrentDictionary<BlockingItem, BlockingCollection<WorkItem>>) =
        printfn $"MONITOR: blockingWorkItemMap count: %i{map.Count}"
