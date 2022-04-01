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
    type Runtime() =
        abstract Eval<'R, 'E> : FIO<'R, 'E> -> Fiber<'R, 'E>

(**********************************************************************************)
(*                                                                                *)
(* Naive runtime                                                                  *)
(*                                                                                *)
(**********************************************************************************)
    type Naive() =
        inherit Runtime()

        member internal this.LowLevelEval(eff : FIO<obj, obj>) : Result<obj, obj> =
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

        override this.Eval<'R, 'E>(eff: FIO<'R, 'E>) : Fiber<'R, 'E> =
            let fiber = new Fiber<'R, 'E>()
            async { fiber.ToLowLevel().Complete <| this.LowLevelEval(eff.Upcast()) }
            |> Async.StartAsTask
            |> ignore
            fiber

(**********************************************************************************)
(*                                                                                *)
(* Advanced runtime                                                               *)
(*                                                                                *)
(**********************************************************************************)
    type Action =
        | RescheduleForRunning
        | RescheduleForBlocking of BlockingItem
        | Evaluated

    and BlockingItem =
        | BlockingChannel of Channel<obj>
        | BlockingFiber of LowLevelFiber

    and WorkItem =
        { Eff: FIO<obj, obj>; LLFiber: LowLevelFiber; PrevAction: Action }
        static member Create(eff, llfiber, prevAction) =
            { Eff = eff; LLFiber = llfiber; PrevAction = prevAction }
        member this.Complete res =
            this.LLFiber.Complete <| res

    and EvalWorker(
            runtime: Advanced,
            workItemQueue: BlockingCollection<WorkItem>,
            blockingWorker: BlockingWorker,
            evalSteps) as self =
        let _ = (async {
            for workItem in workItemQueue.GetConsumingEnumerable() do
                match runtime.LowLevelEval workItem.Eff workItem.PrevAction evalSteps with
                | Success res, Evaluated, _ ->
                    self.CompleteWorkItem(workItem, Ok res)
                | Failure err, Evaluated, _ ->
                    self.CompleteWorkItem(workItem, Error err)
                | eff, RescheduleForRunning, _ ->
                    let workItem = WorkItem.Create(eff, workItem.LLFiber, RescheduleForRunning)
                    self.RescheduleForRunning workItem
                | eff, RescheduleForBlocking blockingItem, _ ->
                    let workItem = WorkItem.Create(eff, workItem.LLFiber, RescheduleForBlocking blockingItem)
                    blockingWorker.RescheduleForBlocking(workItem)
                | _ -> failwith $"EvalWorker: Error occurred while evaluating effect!"
            } |> Async.StartAsTask |> ignore)

        member private _.CompleteWorkItem(workItem, res) =
            workItem.Complete res
            blockingWorker.RescheduleBlockingEffects workItem.LLFiber

        member private _.RescheduleForRunning(workItem) =
            workItemQueue.Add workItem
            
    and BlockingWorker(
            workItemQueue: BlockingCollection<WorkItem>,
            blockingWorkItemMap: BlockingWorkItemMap,
            blockingEventQueue: BlockingCollection<Channel<obj>>) as self =
        let _ = (async {
            for blockingChan in blockingEventQueue.GetConsumingEnumerable() do
                self.HandleBlockingChannel blockingChan
        } |> Async.StartAsTask |> ignore)

        member internal _.RescheduleForBlocking(workItem) =
            blockingWorkItemMap.RescheduleWorkItem workItem

        member private _.HandleBlockingChannel(blockingChan) =
            let blockingItem = BlockingChannel blockingChan
            match blockingWorkItemMap.TryRemove blockingItem with
                | true, (blockingQueue: BlockingCollection<WorkItem>) ->
                    workItemQueue.Add <| blockingQueue.Take()
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

    and BlockingWorkItemMap() =
        let blockingWorkItemMap = new ConcurrentDictionary<BlockingItem, BlockingCollection<WorkItem>>()

        member internal _.RescheduleWorkItem(workItem) =
            let blockingItem =
                match workItem.PrevAction with
                | RescheduleForBlocking blockingItem -> blockingItem
                | _ -> failwith "BlockingWorkItemMap: Attempted to reschedule a work item with illegal previous action!"
            let newBlockingQueue = new BlockingCollection<WorkItem>()
            newBlockingQueue.Add <| workItem
            blockingWorkItemMap.AddOrUpdate(blockingItem, newBlockingQueue, fun _ oldQueue -> oldQueue.Add workItem; oldQueue)
            |> ignore

        member internal _.TryRemove(blockingItem) =
            blockingWorkItemMap.TryRemove blockingItem

        member internal _.Add(blockingItem, blockingQueue) =
            blockingWorkItemMap.AddOrUpdate(blockingItem, blockingQueue, fun _ queue -> queue)
            |> ignore

    and Advanced(evalWorkerCount, evalStepCount) as self =
        inherit Runtime()

        let workItemQueue = new BlockingCollection<WorkItem>()
        let blockingEventQueue = new BlockingCollection<Channel<obj>>()
        let blockingWorkItemMap = new BlockingWorkItemMap()

        do let blockingWorker = self.CreateBlockingWorker()
           self.CreateEvalWorkers(blockingWorker) |> ignore

        new() = Advanced(System.Environment.ProcessorCount, 15)

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
                    workItemQueue.Add <| WorkItem.Create(eff, llfiber, prevAction)
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

        override _.Eval<'R, 'E>(eff: FIO<'R, 'E>) : Fiber<'R, 'E> =
            let fiber = Fiber<'R, 'E>()
            workItemQueue.Add <| WorkItem.Create(eff.Upcast(), fiber.ToLowLevel(), Evaluated)
            fiber

        member private _.CreateBlockingWorker() =
            BlockingWorker(workItemQueue, blockingWorkItemMap, blockingEventQueue)

        member private this.CreateEvalWorkers(blockingWorker) =
            let createEvalWorkers blockingWorker evalSteps start final =
                List.map (fun _ ->
                EvalWorker(this, workItemQueue, blockingWorker, evalSteps))
                    [start..final]
            let (evalWorkerCount, _, evalSteps) = this.GetConfiguration()
            createEvalWorkers blockingWorker evalSteps 0 (evalWorkerCount - 1)

        member _.GetConfiguration() =
            (evalWorkerCount, 1, evalStepCount)
