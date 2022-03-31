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
        | RescheduleRunning
        | RescheduleBlocking of BlockingItem
        | Evaluated

    and BlockingItem =
        | BlockingChannel of Channel<obj>
        | BlockingFiber of LowLevelFiber

    and WorkItem = { Eff: FIO<obj, obj>; LLFiber: LowLevelFiber; PrevAction: Action }
                   static member Create(eff, llfiber, prevAction) =
                       { Eff = eff; LLFiber = llfiber; PrevAction = prevAction }
                   member this.Complete res =
                       this.LLFiber.Complete <| res
                       
    and EvalWorker(
            runtime: Advanced,
            workItemQueue: BlockingCollection<WorkItem>,
            dataEventQueue: BlockingCollection<BlockingItem>,
            blockingWorker: BlockingWorker,
            evalSteps) as self =
        let _ = (async {
            for workItem in workItemQueue.GetConsumingEnumerable() do
                match runtime.LowLevelEval workItem.Eff workItem.PrevAction evalSteps with
                | Success res, Evaluated, _ ->
                    self.CompleteWorkItem(workItem, Ok res)
                | Failure err, Evaluated, _ ->
                    self.CompleteWorkItem(workItem, Error err)
                | eff, RescheduleRunning, _ ->
                    let workItem = WorkItem.Create(eff, workItem.LLFiber, RescheduleRunning)
                    self.RescheduleWorkItem workItem
                | eff, RescheduleBlocking blockingItem, _ ->
                    let workItem = WorkItem.Create(eff, workItem.LLFiber, RescheduleBlocking blockingItem)
                    blockingWorker.RescheduleWorkItem(workItem)
                | _ -> failwith $"EvalWorker: Error occurred while evaluating effect!"
            } |> Async.StartAsTask |> ignore)

        member private _.CompleteWorkItem(workItem, res) =
            workItem.Complete res
            dataEventQueue.Add <| BlockingFiber workItem.LLFiber

        member private _.RescheduleWorkItem(workItem) =
            workItemQueue.Add workItem
            
    and BlockingWorker(
            workItemQueue: BlockingCollection<WorkItem>,
            blockingEventQueue: BlockingCollection<BlockingItem>) as self =
        let blockingWorkItemMap = new BlockingWorkItemMap()

        let _ = (async {
            for blockingItem in blockingEventQueue.GetConsumingEnumerable() do
                self.HandleBlockingItem blockingItem
        } |> Async.StartAsTask |> ignore)

        member internal _.RescheduleWorkItem(workItem) =
            blockingWorkItemMap.RescheduleWorkItem workItem

        member private _.HandleBlockingItem(blockingItem) =
            match blockingWorkItemMap.TryRemove blockingItem with
                | true, blockingQueue ->
                    match blockingItem with
                    | BlockingChannel _ -> self.HandleBlockingChannel(blockingItem, blockingQueue)
                    | BlockingFiber _ -> self.HandleBlockingFiber(blockingQueue)
                | false, _ -> blockingEventQueue.Add blockingItem

        member private _.HandleBlockingChannel(blockingItem, blockingQueue: BlockingCollection<WorkItem>) =
            workItemQueue.Add <| blockingQueue.Take()
            if blockingQueue.Count > 0 then
                blockingWorkItemMap.Add(blockingItem, blockingQueue)

        member private _.HandleBlockingFiber(blockingQueue: BlockingCollection<WorkItem>) =
            while blockingQueue.Count > 0 do
                workItemQueue.Add <| blockingQueue.Take()

    and BlockingWorkItemMap() =
        let blockingWorkItemMap = new ConcurrentDictionary<BlockingItem, BlockingCollection<WorkItem>>()

        member internal _.RescheduleWorkItem(workItem) =
            let blockingItem =
                match workItem.PrevAction with
                | RescheduleBlocking blockingItem -> blockingItem
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

    and Advanced(?evalWorkerCount, ?evalStepCount) as self =
        inherit Runtime()

        let defaultEvalWorkerCount = System.Environment.ProcessorCount
        let defaultBlockingWorkerCount = 1
        let defaultEvalStepCount = 15

        let workItemQueue = new BlockingCollection<WorkItem>()
        let blockingEventQueue = new BlockingCollection<BlockingItem>()

        do self.CreateWorkers()

        member internal _.LowLevelEval eff prevAction evalSteps : FIO<obj, obj> * Action * int =
            if evalSteps = 0 then
                (eff, RescheduleRunning, 0)
            else
                match eff with
                | NonBlocking action ->
                    match action() with
                    | Ok res -> (Success res, Evaluated, evalSteps - 1)
                    | Error err -> (Failure err, Evaluated, evalSteps - 1)
                | Blocking chan ->
                    if prevAction = RescheduleBlocking (BlockingChannel chan) then
                        (Success <| chan.Take(), Evaluated, evalSteps - 1)
                    else
                        (Blocking chan, RescheduleBlocking (BlockingChannel chan), evalSteps)
                | Send (value, chan) ->
                    chan.Add value
                    blockingEventQueue.Add <| BlockingChannel chan
                    (Success value, Evaluated, evalSteps - 1)
                | Concurrent (eff, fiber, llfiber) ->
                    workItemQueue.Add <| WorkItem.Create(eff, llfiber, prevAction)
                    (Success fiber, Evaluated, evalSteps - 1)
                | AwaitFiber llfiber ->
                    if prevAction = RescheduleBlocking (BlockingFiber llfiber) then
                        match llfiber.Await() with
                        | Ok res -> (Success res, Evaluated, evalSteps - 1)
                        | Error err -> (Failure err, Evaluated, evalSteps - 1)
                    else
                        (AwaitFiber llfiber, RescheduleBlocking (BlockingFiber llfiber), evalSteps)
                | Sequence (eff, cont) ->
                    match self.LowLevelEval eff prevAction evalSteps with
                    | Success res, Evaluated, evalSteps -> self.LowLevelEval (cont res) Evaluated evalSteps
                    | Failure err, Evaluated, evalSteps -> (Failure err, Evaluated, evalSteps)
                    | eff, action, evalSteps -> (Sequence (eff, cont), action, evalSteps)
                | SequenceError (eff, cont) ->
                    match self.LowLevelEval eff prevAction evalSteps with
                    | Success res, Evaluated, evalSteps -> (Success res, Evaluated, evalSteps)
                    | Failure err, Evaluated, evalSteps -> self.LowLevelEval (cont err) Evaluated evalSteps
                    | eff, action, evalSteps -> (Sequence (eff, cont), action, evalSteps)
                | Success res ->
                    (Success res, Evaluated, evalSteps - 1)
                | Failure err ->
                    (Failure err, Evaluated, evalSteps - 1)

        override _.Eval<'R, 'E>(eff: FIO<'R, 'E>) : Fiber<'R, 'E> =
            let fiber = Fiber<'R, 'E>()
            workItemQueue.Add <| WorkItem.Create(eff.Upcast(), fiber.ToLowLevel(), Evaluated)
            fiber

        member private _.CreateWorkers() =
            let createEvalWorkers blockingWorker evalSteps start final =
                List.map (fun _ ->
                EvalWorker(self, workItemQueue, blockingEventQueue, blockingWorker, evalSteps)) 
                    [start..final]

            let (evalWorkerCount, _, evalSteps) = self.GetConfiguration()

            let blockingWorker = BlockingWorker(workItemQueue, blockingEventQueue)
            createEvalWorkers blockingWorker evalSteps 0 (evalWorkerCount - 1) |> ignore
            
        member _.GetEvalWorkerCount() =
            match evalWorkerCount with
            | Some evalWorkerCount when evalWorkerCount > 0 -> evalWorkerCount
            | _ -> defaultEvalWorkerCount

        member _.GetBlockingWorkerCount() =
            defaultBlockingWorkerCount

        member _.GetEvalStepCount() =
            match evalStepCount with
            | Some evalStepCount when evalStepCount > 0 -> evalStepCount
            | _ -> defaultEvalStepCount
            
        member _.GetConfiguration() =
            let evalWorkerCount = self.GetEvalWorkerCount()
            let blockingWorkerCount = self.GetBlockingWorkerCount()
            let evalStepCount = self.GetEvalStepCount()
            (evalWorkerCount, blockingWorkerCount, evalStepCount)
