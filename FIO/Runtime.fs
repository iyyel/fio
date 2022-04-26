(**********************************************************************************)
(* FIO - A type-safe, highly concurrent programming library for F#                *)
(* Copyright (c) 2022, Daniel Larsen and Technical University of Denmark (DTU)    *)
(* All rights reserved                                                            *)
(**********************************************************************************)

namespace FSharp.FIO

open FSharp.FIO

open System.Collections.Concurrent

[<AutoOpen>]
module Runtime =

    [<AbstractClass>]
    type Runner() =
        abstract Run<'R, 'E> : FIO<'R, 'E> -> Fiber<'R, 'E>

    #if DETECT_DEADLOCK
    [<AbstractClass>]
    type internal Worker() =
        abstract Working : unit -> bool
    #endif

(**********************************************************************************)
(* Utilities for the runtime                                                      *)
(* DeadLockDetector: System thread that attempts to detect if a deadlock occurs.  *)
(* Monitor:          System thread that monitors crucial data structures.         *)
(**********************************************************************************)
    module internal Utils =

        #if DETECT_DEADLOCK
        type internal DeadlockDetector<'B, 'E when 'B :> Worker and 'E :> Worker>(
            workItemQueue: BlockingCollection<WorkItem>,
            intervalMs: int) as self =
            let blockingItems = new ConcurrentDictionary<BlockingItem, Unit>()
            let mutable blockingWorkers : List<'B> = []
            let mutable evalWorkers : List<'E> = []
            let dataEventQueue = new BlockingCollection<Unit>()
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
                match blockingItems.TryAdd (blockingItem, ()) with
                | true -> dataEventQueue.Add <| ()
                | false -> ()
                
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
         #endif

        #if MONITOR
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
                    let llfiber = workItem.LLFiber
                    printfn $"MONITOR:    ------------ workItem start ------------"
                    printfn $"MONITOR:      WorkItem LLFiber Id: %A{llfiber.Id}"
                    printfn $"MONITOR:      WorkItem LLFiber completed: %A{llfiber.Completed()}"
                    printfn $"MONITOR:      WorkItem LLFiber blocking items count: %A{llfiber.BlockingWorkItemsCount()}"
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
                        printfn $"MONITOR:      Blocking Channel Id: %A{chan.Id}"
                        printfn $"MONITOR:      Blocking Channel count: %A{chan.Count}"
                    | BlockingFiber llfiber ->
                        printfn $"MONITOR:      Blocking LLFiber Id: %A{llfiber.Id}"
                        printfn $"MONITOR:      Blocking LLFiber completed: %A{llfiber.Completed()}"
                        printfn $"MONITOR:      Blocking LLFiber blocking items count: %A{llfiber.BlockingWorkItemsCount()}"
                    let llfiber = workItem.LLFiber
                    printfn $"MONITOR:      WorkItem LLFiber Id: %A{llfiber.Id}"
                    printfn $"MONITOR:      WorkItem LLFiber completed: %A{llfiber.Completed()}"
                    printfn $"MONITOR:      WorkItem LLFiber blocking items count: %A{llfiber.BlockingWorkItemsCount()}"
                    printfn $"MONITOR:      WorkItem PrevAction: %A{workItem.PrevAction}"
                    printfn $"MONITOR:      WorkItem Eff: %A{workItem.Eff}"
                    printfn $"MONITOR:    ------------ BlockingItem * WorkItem end ------------"
                printfn "MONITOR: ------------ workItemQueue information end ------------"
               
            member private _.PrintBlockingEventQueueInfo (queue : BlockingCollection<Channel<obj>>) =
                printfn $"MONITOR: blockingEventQueue count: %i{queue.Count}"
                printfn "MONITOR: ------------ blockingEventQueue information start ------------"
                for blockingChan in queue.ToArray() do
                    printfn $"MONITOR:    ------------ blockingChan start ------------"
                    printfn $"MONITOR:      Id: %A{blockingChan.Id}"
                    printfn $"MONITOR:      Count: %A{blockingChan.Count()}"
                    printfn $"MONITOR:    ------------ blockingChan end ------------"
                printfn "MONITOR: ------------ blockingEventQueue information end ------------"

            member private _.PrintBlockingWorkItemMapInfo (map : ConcurrentDictionary<BlockingItem, BlockingCollection<WorkItem>>) =
                printfn $"MONITOR: blockingWorkItemMap count: %i{map.Count}"
        #endif
        
        // If both flags are disabled, the compiler throws
        // an identation warning as the Utils module will then
        // be empty. The following if's fixes this issue.
        #if !DETECT_DEADLOCK
        #if !MONITOR
            ()
        #endif
        #endif

(**********************************************************************************)
(*                                                                                *)
(* Naive runtime (no arguments)                                                   *)
(*                                                                                *)
(**********************************************************************************)
    module Naive =

        type Runtime() =
            inherit Runner()

            member internal this.LowLevelRun eff : Result<obj, obj> =
                match eff with
                | NonBlocking action ->
                    action ()
                | Blocking chan ->
                    Ok <| chan.Take()
                | SendMessage (value, chan) ->
                    chan.Add value
                    Ok value
                | Concurrent (eff, fiber, llfiber) ->
                    async { llfiber.Complete <| this.LowLevelRun eff }
                    |> Async.StartAsTask
                    |> ignore
                    Ok fiber
                | AwaitFiber llfiber ->
                    llfiber.Await()
                | Sequence (eff, cont) ->
                    match this.LowLevelRun eff with
                    | Ok res -> this.LowLevelRun <| cont res
                    | Error err -> Error err
                | SequenceError (eff, cont) ->
                    match this.LowLevelRun eff with
                    | Ok res -> Ok res
                    | Error err -> this.LowLevelRun <| cont err
                | Success res ->
                    Ok res
                | Failure err ->
                    Error err

            override this.Run<'R, 'E> (eff : FIO<'R, 'E>) : Fiber<'R, 'E> =
                let fiber = new Fiber<'R, 'E>()
                async { fiber.ToLowLevel().Complete <| this.LowLevelRun (eff.Upcast()) }
                |> Async.StartAsTask
                |> ignore
                fiber

(**********************************************************************************)
(*                                                                                *)
(* Intermediate runtime (arguments: EWC, BWC, ESC)                                *)
(*                                                                                *)
(**********************************************************************************)
    module Intermediate =

        type internal EvalWorker(
            runtime: Runtime,
            workItemQueue: BlockingCollection<WorkItem>,
            blockingWorker: BlockingWorker,
            evalSteps) as self =
            #if DETECT_DEADLOCK
            inherit Worker()
            let mutable working = false
            #endif
            let _ = (async {
                for workItem in workItemQueue.GetConsumingEnumerable() do
                    #if DETECT_DEADLOCK
                    working <- true
                    #endif
                    match runtime.LowLevelRun workItem.Eff workItem.PrevAction evalSteps with
                    | Success res, Evaluated, _ ->
                        self.CompleteWorkItem workItem (Ok res)
                    | Failure err, Evaluated, _ ->
                        self.CompleteWorkItem workItem (Error err)
                    | eff, RescheduleForRunning, _ ->
                        let workItem = WorkItem.Create eff workItem.LLFiber RescheduleForRunning
                        workItemQueue.Add workItem
                    | eff, RescheduleForBlocking blockingItem, _ ->
                        let workItem = WorkItem.Create eff workItem.LLFiber (RescheduleForBlocking blockingItem)
                        blockingWorker.RescheduleForBlocking blockingItem workItem
                    | _ -> failwith $"EvalWorker: Error occurred while evaluating effect!"
                    #if DETECT_DEADLOCK
                    working <- false
                    #endif
            } |> Async.StartAsTask |> ignore)

            member private _.CompleteWorkItem workItem res =
                workItem.Complete res

            #if DETECT_DEADLOCK
            override _.Working() =
                working && workItemQueue.Count > 0
            #endif
            
        and internal BlockingWorker(
            workItemQueue: BlockingCollection<WorkItem>,
            #if DETECT_DEADLOCK
            deadlockDetector: Utils.DeadlockDetector<BlockingWorker, EvalWorker>,
            #endif
            blockingItemQueue: BlockingCollection<BlockingItem * WorkItem>) as self =
            #if DETECT_DEADLOCK
            inherit Worker()
            let mutable working = false
            #endif
            let _ = (async {
                for blockingItem, workItem in blockingItemQueue.GetConsumingEnumerable() do
                    #if DETECT_DEADLOCK
                    working <- true
                    #endif
                    self.HandleBlockingItem blockingItem workItem
                    #if DETECT_DEADLOCK
                    working <- false
                    #endif
            } |> Async.StartAsTask |> ignore)

            member private _.HandleBlockingItem blockingItem workItem =
                match blockingItem with
                | BlockingChannel chan ->
                    if chan.DataAvailable() then
                        chan.UseAvailableData()
                        workItemQueue.Add workItem
                        #if DETECT_DEADLOCK
                        deadlockDetector.RemoveBlockingItem blockingItem
                        #endif
                    else 
                        blockingItemQueue.Add ((blockingItem, workItem))
                | BlockingFiber llfiber ->
                    if llfiber.Completed() then
                        workItemQueue.Add workItem
                        #if DETECT_DEADLOCK
                        deadlockDetector.RemoveBlockingItem blockingItem
                        #endif
                    else 
                        blockingItemQueue.Add ((blockingItem, workItem))

            member internal _.RescheduleForBlocking blockingItem workItem =
                blockingItemQueue.Add ((blockingItem, workItem))
                #if DETECT_DEADLOCK
                deadlockDetector.AddBlockingItem blockingItem
                #endif

            #if DETECT_DEADLOCK
            override _.Working() =
                working && workItemQueue.Count > 0
            #endif

        and Runtime(
            evalWorkerCount,
            blockingWorkerCount,
            evalStepCount) as self =
            inherit Runner()

            let workItemQueue = new BlockingCollection<WorkItem>()
            let blockingItemQueue = new BlockingCollection<BlockingItem * WorkItem>()

            #if DETECT_DEADLOCK
            let deadlockDetector = new Utils.DeadlockDetector<BlockingWorker, EvalWorker>(workItemQueue, 500)
            #endif

            do let blockingWorkers = self.CreateBlockingWorkers()
               self.CreateEvalWorkers (List.head blockingWorkers) |> ignore
               #if DETECT_DEADLOCK
               let evalWorkers = self.CreateEvalWorkers (List.head blockingWorkers)
               deadlockDetector.SetBlockingWorkers blockingWorkers
               deadlockDetector.SetEvalWorkers evalWorkers
               #endif
               #if MONITOR
               Utils.Monitor(workItemQueue, Some blockingItemQueue, None, None)
               |> ignore
               #endif

            new() = Runtime(System.Environment.ProcessorCount - 1, 1, 15)

            member internal this.LowLevelRun eff prevAction evalSteps : FIO<obj, obj> * Action * int =
                if evalSteps = 0 then
                    (eff, RescheduleForRunning, 0)
                else
                    match eff with
                    | NonBlocking action ->
                        match action() with
                        | Ok res -> (Success res, Evaluated, evalSteps - 1)
                        | Error err -> (Failure err, Evaluated, evalSteps - 1)
                    | Blocking chan ->
                        if prevAction = RescheduleForBlocking (BlockingChannel chan) then
                            (Success <| chan.Take(), Evaluated, evalSteps - 1)
                        else
                            (Blocking chan, RescheduleForBlocking (BlockingChannel chan), evalSteps)
                    | SendMessage (value, chan) ->
                        chan.Add value
                        (Success value, Evaluated, evalSteps - 1)
                    | Concurrent (eff, fiber, llfiber) ->
                        workItemQueue.Add <| WorkItem.Create eff llfiber prevAction
                        (Success fiber, Evaluated, evalSteps - 1)
                    | AwaitFiber llfiber ->
                        if llfiber.Completed() then
                            match llfiber.Await() with
                            | Ok res -> (Success res, Evaluated, evalSteps - 1)
                            | Error err -> (Failure err, Evaluated, evalSteps - 1)
                        else
                            (AwaitFiber llfiber, RescheduleForBlocking (BlockingFiber llfiber), evalSteps)
                    | Sequence (eff, cont) ->
                        match this.LowLevelRun eff prevAction evalSteps with
                        | Success res, Evaluated, evalSteps -> this.LowLevelRun (cont res) Evaluated evalSteps
                        | Failure err, Evaluated, evalSteps -> (Failure err, Evaluated, evalSteps)
                        | eff, action, evalSteps -> (Sequence (eff, cont), action, evalSteps)
                    | SequenceError (eff, cont) ->
                        match this.LowLevelRun eff prevAction evalSteps with
                        | Success res, Evaluated, evalSteps -> (Success res, Evaluated, evalSteps)
                        | Failure err, Evaluated, evalSteps -> this.LowLevelRun (cont err) Evaluated evalSteps
                        | eff, action, evalSteps -> (Sequence (eff, cont), action, evalSteps)
                    | Success res ->
                        (Success res, Evaluated, evalSteps - 1)
                    | Failure err ->
                        (Failure err, Evaluated, evalSteps - 1)

            override _.Run<'R, 'E> (eff: FIO<'R, 'E>) : Fiber<'R, 'E> =
                let fiber = Fiber<'R, 'E>()
                workItemQueue.Add <| WorkItem.Create (eff.Upcast()) (fiber.ToLowLevel()) Evaluated
                fiber

            member private this.CreateBlockingWorkers() : BlockingWorker list =
                let createBlockingWorkers start final =
                    List.map (fun _ ->
                    #if DETECT_DEADLOCK
                    BlockingWorker(workItemQueue, deadlockDetector, blockingItemQueue)) [start..final]
                    #else
                    BlockingWorker(workItemQueue, blockingItemQueue)) [start..final]
                    #endif
                let _, blockingWorkerCount, _ = this.GetConfiguration()
                createBlockingWorkers 0 (blockingWorkerCount - 1)

            member private this.CreateEvalWorkers blockingWorker : EvalWorker list =
                let createEvalWorkers blockingWorker evalSteps start final =
                    List.map (fun _ ->
                    EvalWorker(this, workItemQueue, blockingWorker, evalSteps)
                    ) [start..final]
                let evalWorkerCount, _, evalStepCount = this.GetConfiguration()
                createEvalWorkers blockingWorker evalStepCount 0 (evalWorkerCount - 1)

            member _.GetConfiguration() =
                let evalWorkerCount =
                    if evalWorkerCount <= 0 then System.Environment.ProcessorCount - 1
                    else evalWorkerCount
                let blockingWorkerCount =
                    if blockingWorkerCount <= 0 then 1
                    else blockingWorkerCount
                let evalStepCount =
                    if evalStepCount <= 0 then 15
                    else evalStepCount
                (evalWorkerCount, blockingWorkerCount, evalStepCount)

(**********************************************************************************)
(*                                                                                *)
(* Advanced runtime (arguments: EWC, BWC, ESC)                                    *)
(*                                                                                *)
(**********************************************************************************)
    module Advanced =
        
        type internal EvalWorker(
            runtime: Runtime,
            workItemQueue: BlockingCollection<WorkItem>,
            blockingWorker: BlockingWorker,
            #if DETECT_DEADLOCK
            deadlockDetector: Utils.DeadlockDetector<BlockingWorker, EvalWorker>,
            #endif
            evalSteps) as self =
            #if DETECT_DEADLOCK
            inherit Worker()
            let mutable working = false
            #endif
            let _ = (async {
                for workItem in workItemQueue.GetConsumingEnumerable() do
                    #if DETECT_DEADLOCK
                    working <- true
                    #endif
                    match runtime.LowLevelRun workItem.Eff workItem.PrevAction evalSteps with
                    | Success res, Evaluated, _ ->
                        self.CompleteWorkItem workItem (Ok res)
                    | Failure err, Evaluated, _ ->
                        self.CompleteWorkItem workItem (Error err)
                    | eff, RescheduleForRunning, _ ->
                        let workItem = WorkItem.Create eff workItem.LLFiber RescheduleForRunning
                        workItemQueue.Add workItem
                    | eff, RescheduleForBlocking blockingItem, _ ->
                        let workItem = WorkItem.Create eff workItem.LLFiber (RescheduleForBlocking blockingItem)
                        blockingWorker.RescheduleForBlocking blockingItem workItem
                        self.HandleBlockingFiber blockingItem
                    | _ -> failwith $"EvalWorker: Error occurred while evaluating effect!"
                    #if DETECT_DEADLOCK
                    working <- false
                    #endif
            } |> Async.StartAsTask |> ignore)

            member private _.CompleteWorkItem workItem res =
                workItem.Complete res
                workItem.LLFiber.RescheduleBlockingWorkItems workItemQueue
                #if DETECT_DEADLOCK
                deadlockDetector.RemoveBlockingItem (BlockingFiber workItem.LLFiber)
                #endif

            member private _.HandleBlockingFiber blockingItem =
                match blockingItem with
                | BlockingFiber llfiber ->
                    if llfiber.Completed() then
                        llfiber.RescheduleBlockingWorkItems workItemQueue
                | _ -> ()

            #if DETECT_DEADLOCK
            override _.Working() =
                working && workItemQueue.Count > 0
            #endif
            
        and internal BlockingWorker(
            workItemQueue: BlockingCollection<WorkItem>,
            #if DETECT_DEADLOCK
            deadlockDetector: Utils.DeadlockDetector<BlockingWorker, EvalWorker>,
            #endif
            blockingEventQueue: BlockingCollection<Channel<obj>>) =
            #if DETECT_DEADLOCK
            inherit Worker()
            let mutable working = false
            #endif
            let _ = (async {
                for blockingChan in blockingEventQueue.GetConsumingEnumerable() do
                    #if DETECT_DEADLOCK
                    working <- true
                    #endif
                    if blockingChan.HasBlockingWorkItems() then
                        blockingChan.RescheduleBlockingWorkItem workItemQueue
                        #if DETECT_DEADLOCK
                        deadlockDetector.RemoveBlockingItem (BlockingChannel blockingChan)
                        #endif
                    else
                        blockingEventQueue.Add blockingChan
                    #if DETECT_DEADLOCK
                    working <- false
                    #endif
            } |> Async.StartAsTask |> ignore)

            member internal _.RescheduleForBlocking blockingItem workItem =
                match blockingItem with
                | BlockingChannel chan ->
                    chan.AddBlockingWorkItem workItem
                | BlockingFiber llfiber ->
                    llfiber.AddBlockingWorkItem workItem
                #if DETECT_DEADLOCK
                deadlockDetector.AddBlockingItem blockingItem
                #endif

            #if DETECT_DEADLOCK
            override _.Working() =
                working && workItemQueue.Count > 0
            #endif

        and Runtime(
            evalWorkerCount,
            blockingWorkerCount,
            evalStepCount) as self =
            inherit Runner()

            let workItemQueue = new BlockingCollection<WorkItem>()
            let blockingEventQueue = new BlockingCollection<Channel<obj>>()

            #if DETECT_DEADLOCK
            let deadlockDetector = new Utils.DeadlockDetector<BlockingWorker, EvalWorker>(workItemQueue, 500)
            #endif

            do let blockingWorkers = self.CreateBlockingWorkers()
               self.CreateEvalWorkers (List.head blockingWorkers) |> ignore
               #if DETECT_DEADLOCK
               let evalWorkers = self.CreateEvalWorkers (List.head blockingWorkers)
               deadlockDetector.SetBlockingWorkers blockingWorkers
               deadlockDetector.SetEvalWorkers evalWorkers
               #endif
               #if MONITOR
               Utils.Monitor(workItemQueue, None, Some blockingEventQueue, None)
               |> ignore
               #endif

            new() = Runtime(System.Environment.ProcessorCount - 1, 1, 15)

            member internal this.LowLevelRun eff prevAction evalSteps : FIO<obj, obj> * Action * int =
                if evalSteps = 0 then
                    (eff, RescheduleForRunning, 0)
                else
                    match eff with
                    | NonBlocking action ->
                        match action() with
                        | Ok res -> (Success res, Evaluated, evalSteps - 1)
                        | Error err -> (Failure err, Evaluated, evalSteps - 1)
                    | Blocking chan ->
                        if prevAction = RescheduleForBlocking (BlockingChannel chan) then
                            (Success <| chan.Take(), Evaluated, evalSteps - 1)
                        else
                            (Blocking chan, RescheduleForBlocking (BlockingChannel chan), evalSteps)
                    | SendMessage (value, chan) ->
                        chan.Add value
                        blockingEventQueue.Add <| chan
                        (Success value, Evaluated, evalSteps - 1)
                    | Concurrent (eff, fiber, llfiber) ->
                        workItemQueue.Add <| WorkItem.Create eff llfiber prevAction
                        (Success fiber, Evaluated, evalSteps - 1)
                    | AwaitFiber llfiber ->
                        if llfiber.Completed() then
                            match llfiber.Await() with
                            | Ok res -> (Success res, Evaluated, evalSteps - 1)
                            | Error err -> (Failure err, Evaluated, evalSteps - 1)
                        else
                            (AwaitFiber llfiber, RescheduleForBlocking (BlockingFiber llfiber), evalSteps)
                    | Sequence (eff, cont) ->
                        match this.LowLevelRun eff prevAction evalSteps with
                        | Success res, Evaluated, evalSteps -> this.LowLevelRun (cont res) Evaluated evalSteps
                        | Failure err, Evaluated, evalSteps -> (Failure err, Evaluated, evalSteps)
                        | eff, action, evalSteps -> (Sequence (eff, cont), action, evalSteps)
                    | SequenceError (eff, cont) ->
                        match this.LowLevelRun eff prevAction evalSteps with
                        | Success res, Evaluated, evalSteps -> (Success res, Evaluated, evalSteps)
                        | Failure err, Evaluated, evalSteps -> this.LowLevelRun (cont err) Evaluated evalSteps
                        | eff, action, evalSteps -> (Sequence (eff, cont), action, evalSteps)
                    | Success res ->
                        (Success res, Evaluated, evalSteps - 1)
                    | Failure err ->
                        (Failure err, Evaluated, evalSteps - 1)

            override _.Run<'R, 'E> (eff: FIO<'R, 'E>) : Fiber<'R, 'E> =
                let fiber = Fiber<'R, 'E>()
                workItemQueue.Add <| WorkItem.Create (eff.Upcast()) (fiber.ToLowLevel()) Evaluated
                fiber

            member private this.CreateBlockingWorkers() : BlockingWorker list =
                let createBlockingWorkers start final =
                    List.map (fun _ ->
                    #if DETECT_DEADLOCK
                    BlockingWorker(workItemQueue, deadlockDetector, blockingEventQueue)) [start..final]
                    #else
                    BlockingWorker(workItemQueue, blockingEventQueue)) [start..final]
                    #endif
                let _, blockingWorkerCount, _ = this.GetConfiguration()
                createBlockingWorkers 0 (blockingWorkerCount - 1)

            member private this.CreateEvalWorkers blockingWorker : EvalWorker list =
                let createEvalWorkers blockingWorker evalSteps start final =
                    List.map (fun _ ->
                    #if DETECT_DEADLOCK
                    EvalWorker(this, workItemQueue, blockingWorker, deadlockDetector, evalSteps)
                    #else
                    EvalWorker(this, workItemQueue, blockingWorker, evalSteps)
                    #endif
                    ) [start..final]
                let evalWorkerCount, _, evalStepCount = this.GetConfiguration()
                createEvalWorkers blockingWorker evalStepCount 0 (evalWorkerCount - 1)

            member _.GetConfiguration() =
                let evalWorkerCount =
                    if evalWorkerCount <= 0 then System.Environment.ProcessorCount - 1
                    else evalWorkerCount
                let blockingWorkerCount =
                    if blockingWorkerCount <= 0 then 1
                    else blockingWorkerCount
                let evalStepCount =
                    if evalStepCount <= 0 then 15
                    else evalStepCount
                (evalWorkerCount, blockingWorkerCount, evalStepCount)

(**********************************************************************************)
(* Deadlocking runtime (arguments: EWC, BWC, ESC)                                 *)
(* This is a version of the advanced runtime that deadlocks sporadically          *)
(* due to a race condition between awaiting and completing fibers.                *)
(**********************************************************************************)
    module Deadlocking =

        type internal EvalWorker(
            runtime: Runtime,
            workItemQueue: BlockingCollection<WorkItem>,
            blockingWorker: BlockingWorker,
            #if DETECT_DEADLOCK
            deadlockDetector: Utils.DeadlockDetector<BlockingWorker, EvalWorker>,
            #endif
            evalSteps) as self =
            #if DETECT_DEADLOCK
            inherit Worker()
            let mutable working = false
            #endif
            let _ = (async {
                for workItem in workItemQueue.GetConsumingEnumerable() do
                    #if DETECT_DEADLOCK
                    working <- true
                    #endif
                    match runtime.LowLevelRun workItem.Eff workItem.PrevAction evalSteps with
                    | Success res, Evaluated, _ ->
                        self.CompleteWorkItem(workItem, Ok res)
                    | Failure err, Evaluated, _ ->
                        self.CompleteWorkItem(workItem, Error err)
                    | eff, RescheduleForRunning, _ ->
                        let workItem = WorkItem.Create eff workItem.LLFiber RescheduleForRunning
                        self.RescheduleForRunning workItem
                    | eff, RescheduleForBlocking blockingItem, _ ->
                        let workItem = WorkItem.Create eff workItem.LLFiber (RescheduleForBlocking blockingItem)
                        blockingWorker.RescheduleForBlocking blockingItem workItem
                    | _ -> failwith $"EvalWorker: Error occurred while evaluating effect!"
                    #if DETECT_DEADLOCK
                    working <- false
                    #endif
                } |> Async.StartAsTask |> ignore)

            member private _.CompleteWorkItem(workItem, res) =
                workItem.Complete res
                blockingWorker.RescheduleBlockingEffects workItem.LLFiber
                #if DETECT_DEADLOCK
                deadlockDetector.RemoveBlockingItem (BlockingFiber workItem.LLFiber)
                #endif

            member private _.RescheduleForRunning(workItem) =
                workItemQueue.Add workItem

            #if DETECT_DEADLOCK
            override _.Working() =
                working && workItemQueue.Count > 0
            #endif
            
        and internal BlockingWorker(
            workItemQueue: BlockingCollection<WorkItem>,
            #if DETECT_DEADLOCK
            deadlockDetector: Utils.DeadlockDetector<BlockingWorker, EvalWorker>,
            #endif
            blockingWorkItemMap: BlockingWorkItemMap,
            blockingEventQueue: BlockingCollection<Channel<obj>>) as self =
            #if DETECT_DEADLOCK
            inherit Worker()
            let mutable working = false
            #endif
            let _ = (async {
                for blockingChan in blockingEventQueue.GetConsumingEnumerable() do
                    #if DETECT_DEADLOCK
                    working <- true
                    #endif
                    self.HandleBlockingChannel blockingChan
                    #if DETECT_DEADLOCK
                    working <- false
                    #endif
            } |> Async.StartAsTask |> ignore)

            member internal _.RescheduleForBlocking blockingItem workItem =
                blockingWorkItemMap.RescheduleWorkItem blockingItem workItem
                #if DETECT_DEADLOCK
                deadlockDetector.AddBlockingItem blockingItem
                #endif

            member private _.HandleBlockingChannel(blockingChan) =
                let blockingItem = BlockingChannel blockingChan
                match blockingWorkItemMap.TryRemove blockingItem with
                    | true, (blockingQueue: BlockingCollection<WorkItem>) ->
                        workItemQueue.Add <| blockingQueue.Take()
                        #if DETECT_DEADLOCK
                        deadlockDetector.RemoveBlockingItem blockingItem
                        #endif
                        if blockingQueue.Count > 0 then
                            blockingWorkItemMap.Add(blockingItem, blockingQueue)
                    | false, _ -> blockingEventQueue.Add blockingChan

            member internal _.RescheduleBlockingEffects(llfiber) =
                let blockingItem = BlockingFiber llfiber
                match blockingWorkItemMap.TryRemove blockingItem with
                    | true, (blockingQueue: BlockingCollection<WorkItem>) ->
                        while blockingQueue.Count > 0 do
                            workItemQueue.Add <| blockingQueue.Take()
                    | false, _ -> ()

            #if DETECT_DEADLOCK
            override _.Working() =
                working && workItemQueue.Count > 0
            #endif

        and internal BlockingWorkItemMap() =
            let blockingWorkItemMap = new ConcurrentDictionary<BlockingItem, BlockingCollection<WorkItem>>()

            member internal _.RescheduleWorkItem blockingItem workItem =
                let newBlockingQueue = new BlockingCollection<WorkItem>()
                newBlockingQueue.Add <| workItem
                blockingWorkItemMap.AddOrUpdate(blockingItem, newBlockingQueue, fun _ oldQueue -> oldQueue.Add workItem; oldQueue)
                |> ignore

            member internal _.TryRemove(blockingItem) =
                blockingWorkItemMap.TryRemove blockingItem

            member internal _.Add(blockingItem, blockingQueue) =
                blockingWorkItemMap.AddOrUpdate(blockingItem, blockingQueue, fun _ queue -> queue)
                |> ignore

            member internal _.Get() : ConcurrentDictionary<BlockingItem, BlockingCollection<WorkItem>> =
                blockingWorkItemMap

        and Runtime(
            evalWorkerCount,
            blockingWorkerCount,
            evalStepCount) as self =
            inherit Runner()

            let workItemQueue = new BlockingCollection<WorkItem>()
            let blockingEventQueue = new BlockingCollection<Channel<obj>>()
            let blockingWorkItemMap = new BlockingWorkItemMap()

            #if DETECT_DEADLOCK
            let deadlockDetector = new Utils.DeadlockDetector<BlockingWorker, EvalWorker>(workItemQueue, 500)
            #endif

            do let blockingWorkers = self.CreateBlockingWorkers()
               self.CreateEvalWorkers (List.head blockingWorkers) |> ignore
               #if DETECT_DEADLOCK
               let evalWorkers = self.CreateEvalWorkers (List.head blockingWorkers)
               deadlockDetector.SetBlockingWorkers blockingWorkers
               deadlockDetector.SetEvalWorkers evalWorkers
               #endif
               #if MONITOR
               Utils.Monitor(workItemQueue, None, Some blockingEventQueue, Some <| blockingWorkItemMap.Get())
               |> ignore
               #endif

            new() = Runtime(System.Environment.ProcessorCount - 1, 1, 15)

            member internal this.LowLevelRun eff prevAction evalSteps : FIO<obj, obj> * Action * int =
                if evalSteps = 0 then
                    (eff, RescheduleForRunning, 0)
                else
                    match eff with
                    | NonBlocking action ->
                        match action() with
                        | Ok res -> (Success res, Evaluated, evalSteps - 1)
                        | Error err -> (Failure err, Evaluated, evalSteps - 1)
                    | Blocking chan ->
                        if prevAction = RescheduleForBlocking (BlockingChannel chan) then
                            (Success <| chan.Take(), Evaluated, evalSteps - 1)
                        else
                            (Blocking chan, RescheduleForBlocking (BlockingChannel chan), evalSteps)
                    | SendMessage (value, chan) ->
                        chan.Add value
                        blockingEventQueue.Add <| chan
                        (Success value, Evaluated, evalSteps - 1)
                    | Concurrent (eff, fiber, llfiber) ->
                        workItemQueue.Add <| WorkItem.Create eff llfiber prevAction
                        (Success fiber, Evaluated, evalSteps - 1)
                    | AwaitFiber llfiber ->
                        if llfiber.Completed() then
                            match llfiber.Await() with
                            | Ok res -> (Success res, Evaluated, evalSteps - 1)
                            | Error err -> (Failure err, Evaluated, evalSteps - 1)
                        else
                            (AwaitFiber llfiber, RescheduleForBlocking (BlockingFiber llfiber), evalSteps)
                    | Sequence (eff, cont) ->
                        match this.LowLevelRun eff prevAction evalSteps with
                        | Success res, Evaluated, evalSteps -> this.LowLevelRun (cont res) Evaluated evalSteps
                        | Failure err, Evaluated, evalSteps -> (Failure err, Evaluated, evalSteps)
                        | eff, action, evalSteps -> (Sequence (eff, cont), action, evalSteps)
                    | SequenceError (eff, cont) ->
                        match this.LowLevelRun eff prevAction evalSteps with
                        | Success res, Evaluated, evalSteps -> (Success res, Evaluated, evalSteps)
                        | Failure err, Evaluated, evalSteps -> this.LowLevelRun (cont err) Evaluated evalSteps
                        | eff, action, evalSteps -> (Sequence (eff, cont), action, evalSteps)
                    | Success res ->
                        (Success res, Evaluated, evalSteps - 1)
                    | Failure err ->
                        (Failure err, Evaluated, evalSteps - 1)

            override _.Run<'R, 'E>(eff: FIO<'R, 'E>) : Fiber<'R, 'E> =
                let fiber = Fiber<'R, 'E>()
                workItemQueue.Add <| WorkItem.Create (eff.Upcast()) (fiber.ToLowLevel()) Evaluated
                fiber

            member private this.CreateBlockingWorkers() =
                let createBlockingWorkers start final =
                    List.map (fun _ ->
                    #if DETECT_DEADLOCK
                    BlockingWorker(workItemQueue, deadlockDetector, blockingWorkItemMap, blockingEventQueue)) [start..final]
                    #else
                    BlockingWorker(workItemQueue, blockingWorkItemMap, blockingEventQueue)) [start..final]
                    #endif
                let _, blockingWorkerCount, _ = this.GetConfiguration()
                createBlockingWorkers 0 (blockingWorkerCount - 1)

            member private this.CreateEvalWorkers blockingWorker =
                let createEvalWorkers blockingWorker evalSteps start final =
                    List.map (fun _ ->
                    #if DETECT_DEADLOCK
                    EvalWorker(this, workItemQueue, blockingWorker, deadlockDetector, evalSteps)
                    #else
                    EvalWorker(this, workItemQueue, blockingWorker, evalSteps)
                    #endif
                    ) [start..final]
                let evalWorkerCount, _, evalStepCount = this.GetConfiguration()
                createEvalWorkers blockingWorker evalStepCount 0 (evalWorkerCount - 1)

            member _.GetConfiguration() =
                let evalWorkerCount =
                    if evalWorkerCount <= 0 then System.Environment.ProcessorCount - 1
                    else evalWorkerCount
                let blockingWorkerCount =
                    if blockingWorkerCount <= 0 then 1
                    else blockingWorkerCount
                let evalStepCount =
                    if evalStepCount <= 0 then 15
                    else evalStepCount
                (evalWorkerCount, blockingWorkerCount, evalStepCount)
