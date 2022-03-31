(**********************************************************************************)
(* FIO - Effectful programming library for F#                                     *)
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
    type BlockingItem =
        | BlockingChannel of Channel<obj>
        | BlockingFiber of LowLevelFiber

    and Action =
        | RescheduleRunning
        | RescheduleBlocking of BlockingItem
        | Finished

    and WorkItem = { Eff: FIO<obj, obj>; LLFiber: LowLevelFiber; PrevAction: Action }
                   static member Create(eff, llfiber,prevAction ) =
                       { Eff = eff; LLFiber = llfiber; PrevAction = prevAction }

    and EvalWorker(
            runtime: Advanced,
            workItemQueue: BlockingCollection<WorkItem>,
            dataEventQueue: BlockingCollection<BlockingItem>,
            blockingWorker: BlockingWorker,
            evalSteps) as self =
        let _ = (async {
            for workItem in workItemQueue.GetConsumingEnumerable() do
                match runtime.LowLevelEval workItem.Eff workItem.PrevAction evalSteps with
                | Success res, Finished, _ ->
                    self.CompleteWorkItem(workItem, Ok res)
                | Failure err, Finished, _ ->
                    self.CompleteWorkItem(workItem, Error err)
                | eff, RescheduleRunning, _ ->
                    let workItem = WorkItem.Create(eff, workItem.LLFiber, RescheduleRunning)
                    self.RescheduleWorkItem workItem
                | eff, RescheduleBlocking blocker, _ ->
                    let workItem = WorkItem.Create(eff, workItem.LLFiber, RescheduleBlocking blocker)
                    blockingWorker.RescheduleWorkItem(workItem)
                | _ -> failwith $"EvalWorker: Error occurred while evaluating effect!"
            } |> Async.StartAsTask |> ignore)

        member internal _.CompleteWorkItem(workItem: WorkItem, res: Result<obj, obj>) =
            workItem.LLFiber.Complete <| res
            dataEventQueue.Add <| BlockingFiber workItem.LLFiber

        member internal _.RescheduleWorkItem(workItem: WorkItem) =
            workItemQueue.Add workItem
            
    and BlockingWorker(
            workItemQueue: BlockingCollection<WorkItem>,
            dataEventQueue: BlockingCollection<BlockingItem>) as self =
        let blockingWorkItemMap = new BlockingWorkItemMap()
        let _ = (async {
            for blocker in dataEventQueue.GetConsumingEnumerable() do
                self.HandleBlocker blocker
            } |> Async.StartAsTask |> ignore)

        member internal _.RescheduleWorkItem(workItem: WorkItem) =
            blockingWorkItemMap.RescheduleWorkItem workItem

        member private this.HandleBlocker(blocker: BlockingItem) =
            match blockingWorkItemMap.TryRemove blocker with
                | true, blockingQueue -> 
                    match blocker with
                    | BlockingChannel _ -> this.HandleBlockingChannel(blocker, blockingQueue)
                    | BlockingFiber _ -> this.HandleBlockingFiber(blockingQueue)
                | false, _ -> dataEventQueue.Add blocker

        member private _.HandleBlockingChannel(blocker: BlockingItem, blockingQueue: BlockingCollection<WorkItem>) =
            workItemQueue.Add <| blockingQueue.Take()
            if blockingQueue.Count > 0 then
                blockingWorkItemMap.AddOrUpdate(blocker, blockingQueue)
                |> ignore

        member private _.HandleBlockingFiber(blockingQueue: BlockingCollection<WorkItem>) =
            while blockingQueue.Count > 0 do
                workItemQueue.Add <| blockingQueue.Take()

    and BlockingWorkItemMap() =
        let blockingWorkItemMap = new ConcurrentDictionary<BlockingItem, BlockingCollection<WorkItem>>()

        member internal _.RescheduleWorkItem(workItem: WorkItem) =
            let blocker = match workItem.PrevAction with
                          | RescheduleBlocking blocker -> blocker
                          | _ -> failwith "BlockingWorkItemMap: Attempted to reschedule a work item with wrong previous action!"
            let newQueue = new BlockingCollection<WorkItem>()
            newQueue.Add <| workItem
            blockingWorkItemMap.AddOrUpdate(blocker, newQueue, fun _ oldQueue -> oldQueue.Add workItem; oldQueue)
            |> ignore

        member internal _.TryRemove(blocker: BlockingItem) =
            blockingWorkItemMap.TryRemove blocker

        member internal _.AddOrUpdate(blocker: BlockingItem, blockingQueue: BlockingCollection<WorkItem>) =
            blockingWorkItemMap.AddOrUpdate(blocker, blockingQueue, fun _ queue -> queue)

    and Advanced(?evalWorkerCount, ?evalStepCount) as self =
        inherit Runtime()

        let defaultEvalWorkerCount = System.Environment.ProcessorCount / 2
        let defaultBlockingWorkerCount = 1
        let defaultEvalStepCount = 15

        let workItemQueue = new BlockingCollection<WorkItem>()
        let dataEventQueue = new BlockingCollection<BlockingItem>()

        do self.CreateWorkers()

        member internal _.LowLevelEval (eff: FIO<obj, obj>) (prevAction : Action) 
                (evalSteps: int) : FIO<obj, obj> * Action * int =
            if evalSteps = 0 then
                (eff, RescheduleRunning, 0)
            else
                match eff with
                | NonBlocking action ->
                    match action() with
                    | Ok res -> (Success res, Finished, evalSteps - 1)
                    | Error err -> (Failure err, Finished, evalSteps - 1)
                | Blocking chan ->
                    if prevAction = RescheduleBlocking (BlockingChannel chan) then
                        (Success <| chan.Take(), Finished, evalSteps - 1)
                    else
                        (Blocking chan, RescheduleBlocking (BlockingChannel chan), evalSteps)
                | Send (value, chan) ->
                    chan.Add value
                    dataEventQueue.Add <| BlockingChannel chan
                    (Success value, Finished, evalSteps - 1)
                | Concurrent (eff, fiber, llfiber) ->
                    workItemQueue.Add <| WorkItem.Create(eff, llfiber, prevAction)
                    (Success fiber, Finished, evalSteps - 1)
                | AwaitFiber llfiber ->
                    if prevAction = RescheduleBlocking (BlockingFiber llfiber) then
                        match llfiber.Await() with
                        | Ok res -> (Success res, Finished, evalSteps - 1)
                        | Error err -> (Failure err, Finished, evalSteps - 1)
                    else
                        (AwaitFiber llfiber, RescheduleBlocking (BlockingFiber llfiber), evalSteps)
                | Sequence (eff, cont) ->
                    match self.LowLevelEval eff prevAction evalSteps with
                    | Success res, Finished, evalSteps -> self.LowLevelEval (cont res) Finished evalSteps
                    | Failure err, Finished, evalSteps -> (Failure err, Finished, evalSteps)
                    | eff, action, evalSteps -> (Sequence (eff, cont), action, evalSteps)
                | SequenceError (eff, cont) ->
                    match self.LowLevelEval eff prevAction evalSteps with
                    | Success res, Finished, evalSteps -> (Success res, Finished, evalSteps)
                    | Failure err, Finished, evalSteps -> self.LowLevelEval (cont err) Finished evalSteps
                    | eff, action, evalSteps -> (Sequence (eff, cont), action, evalSteps)
                | Success res ->
                    (Success res, Finished, evalSteps - 1)
                | Failure err ->
                    (Failure err, Finished, evalSteps - 1)

        override _.Eval<'R, 'E>(eff: FIO<'R, 'E>) : Fiber<'R, 'E> =
            let fiber = Fiber<'R, 'E>()
            workItemQueue.Add <| WorkItem.Create(eff.Upcast(), fiber.ToLowLevel(), Finished)
            fiber

        member private _.CreateWorkers() =
            let createEvalWorkers blockingWorker evalSteps start final =
                List.map (fun _ ->
                EvalWorker(self, workItemQueue, dataEventQueue, blockingWorker, evalSteps)) 
                    [start..final]

            let (evalWorkerCount, _, evalSteps) = self.GetConfiguration()

            let blockingWorker = BlockingWorker(workItemQueue, dataEventQueue)
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
            let evalSteps = self.GetEvalStepCount()
            (evalWorkerCount, blockingWorkerCount, evalSteps)
