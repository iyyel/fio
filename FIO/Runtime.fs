(**********************************************************************************)
(* FIO - A type-safe, highly concurrent programming library for F#                *)
(* Copyright (c) 2022, Daniel Larsen and Technical University of Denmark (DTU)    *)
(* All rights reserved                                                            *)
(**********************************************************************************)

namespace FSharp.FIO

open FSharp.FIO.FIO

open System.Collections.Concurrent

module Runtime =

    [<AbstractClass>]
    type Evaluator() =
        abstract Eval<'R, 'E> : FIO<'R, 'E> -> Fiber<'R, 'E>

(**********************************************************************************)
(*                                                                                *)
(* Naive runtime (no arguments)                                                   *)
(*                                                                                *)
(**********************************************************************************)
    module Naive = 

        type Runtime() =
            inherit Evaluator()

            member internal this.LowLevelEval eff : Result<obj, obj> =
                match eff with
                | NonBlocking action ->
                    action ()
                | Blocking chan ->
                    Ok <| chan.Take()
                | Send (value, chan) ->
                    chan.Add value
                    Ok value
                | Concurrent (eff, fiber, llfiber) ->
                    async { llfiber.Complete <| this.LowLevelEval eff }
                    |> Async.StartAsTask
                    |> ignore
                    Ok fiber
                | AwaitFiber llfiber ->
                    llfiber.Await()
                | Sequence (eff, cont) ->
                    match this.LowLevelEval eff with
                    | Ok res -> this.LowLevelEval <| cont res
                    | Error err -> Error err
                | SequenceError (eff, cont) ->
                    match this.LowLevelEval eff with
                    | Ok res -> Ok res
                    | Error err -> this.LowLevelEval <| cont err
                | Success res ->
                    Ok res
                | Failure err ->
                    Error err

            override this.Eval<'R, 'E> (eff : FIO<'R, 'E>) : Fiber<'R, 'E> =
                let fiber = new Fiber<'R, 'E>()
                async { fiber.ToLowLevel().Complete <| this.LowLevelEval (eff.Upcast()) }
                |> Async.StartAsTask
                |> ignore
                fiber

(**********************************************************************************)
(*                                                                                *)
(* Intermediate runtime (arguments: EWC, BWC, ESC)                                *)
(*                                                                                *)
(**********************************************************************************)
    module Intermediate =

        type internal Monitor(
            workItemQueue: BlockingCollection<WorkItem>,
            blockingEventQueue: BlockingCollection<Channel<obj>>) as self =
            let _ = (async {
                while true do
                    printfn $"\n\nMONITOR: workItemQueue count: %i{workItemQueue.Count}"
                    printfn $"MONITOR: blockingEventQueue count: %i{blockingEventQueue.Count}\n"
                    self.PrintBlockingEventQueueContent()
                    printfn "\n\n"
                    System.Threading.Thread.Sleep(1000)
            } |> Async.StartAsTask |> ignore)

            member private _.PrintBlockingEventQueueContent() =
                printfn "MONITOR: ------------ blockingEventQueue contents start ------------"
                for blockingChan in blockingEventQueue.ToArray() do
                    printfn "MONITOR: ------------ blockingChan start ------------"
                    printfn $"MONITOR: Id: %A{blockingChan.Id}"
                    printfn $"MONITOR: Count: %A{blockingChan.Count()}"
                    printfn "MONITOR: ------------ blockingChan end ------------"
                printfn "MONITOR: ------------ blockingEventQueue contents end ------------"

        and internal EvalWorker(
            runtime: Runtime,
            workItemQueue: BlockingCollection<WorkItem>,
            blockingWorker: BlockingWorker,
            evalSteps) as self =
            let _ = (async {
                for workItem in workItemQueue.GetConsumingEnumerable() do
                    match runtime.LowLevelEval workItem.Eff workItem.PrevAction evalSteps with
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
            } |> Async.StartAsTask |> ignore)

            member private _.CompleteWorkItem workItem res =
                workItem.Complete res
                workItem.LLFiber.RescheduleBlockingWorkItems workItemQueue

            member private _.HandleBlockingFiber blockingItem =
                match blockingItem with
                | BlockingFiber llfiber ->
                    if llfiber.Completed() then
                        llfiber.RescheduleBlockingWorkItems workItemQueue
                | _ -> ()
            
        and internal BlockingWorker(
            workItemQueue: BlockingCollection<WorkItem>,
            blockingItemQueue: BlockingCollection<BlockingItem * WorkItem>) as self =
            let _ = (async {
                for blockingItem, workItem in blockingItemQueue.GetConsumingEnumerable() do
                    self.HandleBlockingItem blockingItem workItem
            } |> Async.StartAsTask |> ignore)

            member private _.HandleBlockingItem blockingItem workItem =
                match blockingItem with
                | BlockingChannel chan ->
                    if chan.DataAvailable() then
                        chan.UseAvailableData()
                        workItemQueue.Add workItem
                    else blockingItemQueue.Add ((blockingItem, workItem))
                | BlockingFiber llfiber ->
                    if llfiber.Completed() then workItemQueue.Add workItem
                    else blockingItemQueue.Add ((blockingItem, workItem))

            member internal _.RescheduleForBlocking blockingItem workItem =
                blockingItemQueue.Add ((blockingItem, workItem))

        and Runtime(evalWorkerCount,
                     blockingWorkerCount,
                     evalStepCount) as self =
            inherit Evaluator()

            let workItemQueue = new BlockingCollection<WorkItem>()
            let blockingItemQueue = new BlockingCollection<BlockingItem * WorkItem>()

            do let blockingWorkers = self.CreateBlockingWorkers()
               self.CreateEvalWorkers (List.head blockingWorkers) |> ignore
               //Monitor(workItemQueue, blockingEventQueue, blockingWorkItemMap) |> ignore

            new() = Runtime(System.Environment.ProcessorCount, 1, 15)

            member internal this.LowLevelEval eff prevAction evalSteps : FIO<obj, obj> * Action * int =
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
                    | Send (value, chan) ->
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
                        match this.LowLevelEval eff prevAction evalSteps with
                        | Success res, Evaluated, evalSteps -> this.LowLevelEval (cont res) Evaluated evalSteps
                        | Failure err, Evaluated, evalSteps -> (Failure err, Evaluated, evalSteps)
                        | eff, action, evalSteps -> (Sequence (eff, cont), action, evalSteps)
                    | SequenceError (eff, cont) ->
                        match this.LowLevelEval eff prevAction evalSteps with
                        | Success res, Evaluated, evalSteps -> (Success res, Evaluated, evalSteps)
                        | Failure err, Evaluated, evalSteps -> this.LowLevelEval (cont err) Evaluated evalSteps
                        | eff, action, evalSteps -> (Sequence (eff, cont), action, evalSteps)
                    | Success res ->
                        (Success res, Evaluated, evalSteps - 1)
                    | Failure err ->
                        (Failure err, Evaluated, evalSteps - 1)

            override _.Eval<'R, 'E> (eff: FIO<'R, 'E>) : Fiber<'R, 'E> =
                let fiber = Fiber<'R, 'E>()
                workItemQueue.Add <| WorkItem.Create (eff.Upcast()) (fiber.ToLowLevel()) Evaluated
                fiber

            member private _.CreateBlockingWorkers() =
                let createBlockingWorkers start final =
                    List.map (fun _ ->
                    BlockingWorker(workItemQueue, blockingItemQueue))
                        [start..final]
                let _, blockingWorkerCount, _ = self.GetConfiguration()
                createBlockingWorkers 0 (blockingWorkerCount - 1)

            member private this.CreateEvalWorkers blockingWorker =
                let createEvalWorkers blockingWorker evalSteps start final =
                    List.map (fun _ ->
                    EvalWorker(this, workItemQueue, blockingWorker, evalSteps))
                        [start..final]
                let evalWorkerCount, _, evalStepCount = this.GetConfiguration()
                createEvalWorkers blockingWorker evalStepCount 0 (evalWorkerCount - 1)

            member _.GetConfiguration() =
                let evalWorkerCount =
                    if evalWorkerCount <= 0 then System.Environment.ProcessorCount
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
(* (arguments: EWC, BWC, ESC)                                                     *)
(*                                                                                *)
(**********************************************************************************)
    module Advanced =

        type internal Monitor(
            workItemQueue: BlockingCollection<WorkItem>,
            blockingEventQueue: BlockingCollection<Channel<obj>>) as self =
            let _ = (async {
                while true do
                    printfn $"\n\nMONITOR: workItemQueue count: %i{workItemQueue.Count}"
                    printfn $"MONITOR: blockingEventQueue count: %i{blockingEventQueue.Count}\n"
                    self.PrintBlockingEventQueueContent()
                    printfn "\n\n"
                    System.Threading.Thread.Sleep(1000)
            } |> Async.StartAsTask |> ignore)

            member private _.PrintBlockingEventQueueContent() =
                printfn "MONITOR: ------------ blockingEventQueue contents start ------------"
                for blockingChan in blockingEventQueue.ToArray() do
                    printfn "MONITOR: ------------ blockingChan start ------------"
                    printfn $"MONITOR: Id: %A{blockingChan.Id}"
                    printfn $"MONITOR: Count: %A{blockingChan.Count()}"
                    printfn "MONITOR: ------------ blockingChan end ------------"
                printfn "MONITOR: ------------ blockingEventQueue contents end ------------"

        and internal EvalWorker(
            runtime: Runtime,
            workItemQueue: BlockingCollection<WorkItem>,
            blockingWorker: BlockingWorker,
            evalSteps) as self =
            let _ = (async {
                for workItem in workItemQueue.GetConsumingEnumerable() do
                    match runtime.LowLevelEval workItem.Eff workItem.PrevAction evalSteps with
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
            } |> Async.StartAsTask |> ignore)

            member private _.CompleteWorkItem workItem res =
                workItem.Complete res
                workItem.LLFiber.RescheduleBlockingWorkItems workItemQueue

            member private _.HandleBlockingFiber blockingItem =
                match blockingItem with
                | BlockingFiber llfiber ->
                    if llfiber.Completed() then
                        llfiber.RescheduleBlockingWorkItems workItemQueue
                | _ -> ()
            
        and internal BlockingWorker(
            workItemQueue: BlockingCollection<WorkItem>,
            blockingEventQueue: BlockingCollection<Channel<obj>>) =
            let _ = (async {
                for blockingChan in blockingEventQueue.GetConsumingEnumerable() do
                    if blockingChan.HasBlockingWorkItems() then
                        blockingChan.RescheduleBlockingWorkItem workItemQueue
                    else
                        blockingEventQueue.Add blockingChan
            } |> Async.StartAsTask |> ignore)

            member internal _.RescheduleForBlocking blockingItem workItem =
                match blockingItem with
                | BlockingChannel chan ->
                    chan.AddBlockingWorkItem workItem
                | BlockingFiber llfiber ->
                    llfiber.AddBlockingWorkItem workItem

        and Runtime(evalWorkerCount,
                     blockingWorkerCount,
                     evalStepCount) as self =
            inherit Evaluator()

            let workItemQueue = new BlockingCollection<WorkItem>()
            let blockingEventQueue = new BlockingCollection<Channel<obj>>()

            do let blockingWorkers = self.CreateBlockingWorkers()
               self.CreateEvalWorkers (List.head blockingWorkers) |> ignore
               //Monitor(workItemQueue, blockingEventQueue, blockingWorkItemMap) |> ignore

            new() = Runtime(System.Environment.ProcessorCount, 1, 15)

            member internal this.LowLevelEval eff prevAction evalSteps : FIO<obj, obj> * Action * int =
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
                    | Send (value, chan) ->
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
                        match this.LowLevelEval eff prevAction evalSteps with
                        | Success res, Evaluated, evalSteps -> this.LowLevelEval (cont res) Evaluated evalSteps
                        | Failure err, Evaluated, evalSteps -> (Failure err, Evaluated, evalSteps)
                        | eff, action, evalSteps -> (Sequence (eff, cont), action, evalSteps)
                    | SequenceError (eff, cont) ->
                        match this.LowLevelEval eff prevAction evalSteps with
                        | Success res, Evaluated, evalSteps -> (Success res, Evaluated, evalSteps)
                        | Failure err, Evaluated, evalSteps -> this.LowLevelEval (cont err) Evaluated evalSteps
                        | eff, action, evalSteps -> (Sequence (eff, cont), action, evalSteps)
                    | Success res ->
                        (Success res, Evaluated, evalSteps - 1)
                    | Failure err ->
                        (Failure err, Evaluated, evalSteps - 1)

            override _.Eval<'R, 'E> (eff: FIO<'R, 'E>) : Fiber<'R, 'E> =
                let fiber = Fiber<'R, 'E>()
                workItemQueue.Add <| WorkItem.Create (eff.Upcast()) (fiber.ToLowLevel()) Evaluated
                fiber

            member private _.CreateBlockingWorkers() =
                let createBlockingWorkers start final =
                    List.map (fun _ ->
                    BlockingWorker(workItemQueue, blockingEventQueue))
                        [start..final]
                let _, blockingWorkerCount, _ = self.GetConfiguration()
                createBlockingWorkers 0 (blockingWorkerCount - 1)

            member private this.CreateEvalWorkers blockingWorker =
                let createEvalWorkers blockingWorker evalSteps start final =
                    List.map (fun _ ->
                    EvalWorker(this, workItemQueue, blockingWorker, evalSteps))
                        [start..final]
                let evalWorkerCount, _, evalStepCount = this.GetConfiguration()
                createEvalWorkers blockingWorker evalStepCount 0 (evalWorkerCount - 1)

            member _.GetConfiguration() =
                let evalWorkerCount =
                    if evalWorkerCount <= 0 then System.Environment.ProcessorCount
                    else evalWorkerCount
                let blockingWorkerCount =
                    if blockingWorkerCount <= 0 then 1
                    else blockingWorkerCount
                let evalStepCount =
                    if evalStepCount <= 0 then 15
                    else evalStepCount
                (evalWorkerCount, blockingWorkerCount, evalStepCount)
