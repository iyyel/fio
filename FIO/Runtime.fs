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
    type Blocker =
        | BlockingChannel of Channel<obj>
        | BlockingFiber of LowLevelFiber

    and Action =
        | RescheduleRun
        | RescheduleBlock of Blocker
        | Finished
        | None

    and WorkItem = { Eff: FIO<obj, obj>; LLFiber: LowLevelFiber; PrevAction: Action }
                   static member Create(eff, llfiber,prevAction ) =
                       { Eff = eff; LLFiber = llfiber; PrevAction = prevAction }

    and EvalWorker(
            id,
            runtime: Advanced,
            workItemQueue: BlockingCollection<WorkItem>,
            blockingWorkItemMap: ConcurrentDictionary<Blocker, WorkItem>,
            dataEventQueue: BlockingCollection<Blocker>,
            userFibersMap: ConcurrentDictionary<LowLevelFiber, Unit>,
            evalSteps) =
        let _ = (async {
            for workItem in workItemQueue.GetConsumingEnumerable() do
                match runtime.LowLevelEval workItem.Eff workItem.PrevAction evalSteps with
                | Success res, Finished, _ ->
                    workItem.LLFiber.Complete <| Ok res
                    if userFibersMap.ContainsKey workItem.LLFiber then
                        dataEventQueue.Add <| BlockingFiber workItem.LLFiber
                        userFibersMap.TryRemove workItem.LLFiber |> ignore
                | Failure err, Finished, _ ->
                    workItem.LLFiber.Complete <| Error err
                    if userFibersMap.ContainsKey workItem.LLFiber then
                        dataEventQueue.Add <| BlockingFiber workItem.LLFiber
                        userFibersMap.TryRemove workItem.LLFiber |> ignore
                | eff, RescheduleRun, _ ->
                    workItemQueue.Add <| WorkItem.Create(eff, workItem.LLFiber, RescheduleRun)
                | eff, RescheduleBlock blocker, _ ->
                    blockingWorkItemMap.TryAdd (blocker, WorkItem.Create(eff, workItem.LLFiber, RescheduleBlock blocker))
                    |> ignore
                | _ -> failwith $"EvalWorker(%s{id}): Error occurred while evaluating effect!"
            } |> Async.StartAsTask |> ignore)
        
    and BlockingWorker(
            id,
            workItemQueue: BlockingCollection<WorkItem>,
            blockingWorkItemMap: ConcurrentDictionary<Blocker, WorkItem>,
            dataEventQueue: BlockingCollection<Blocker>) =
        let _ = (async {
            for blocker in dataEventQueue.GetConsumingEnumerable() do
                match blockingWorkItemMap.TryRemove blocker with
                | true, workItem -> workItemQueue.Add workItem
                | false, _ -> dataEventQueue.Add blocker
             } |> Async.StartAsTask |> ignore)

    and Advanced(?evalWorkerCount, ?blockingWorkerCount, ?evalSteps) as self =
        inherit Runtime()
        let defaultEvalWorkerCount = System.Environment.ProcessorCount / 2
        let defaultBlockingWorkerCount = System.Environment.ProcessorCount / 2
        let defaultEvalSteps = 15

        let workItemQueue = new BlockingCollection<WorkItem>()
        let blockingWorkItemMap = new ConcurrentDictionary<Blocker, WorkItem>()
        let dataEventQueue = new BlockingCollection<Blocker>()
        let userFibersMap = new ConcurrentDictionary<LowLevelFiber, Unit>()

        do self.CreateWorkers()

        member internal this.LowLevelEval (eff: FIO<obj, obj>) (prevAction : Action) (evalSteps: int) : FIO<obj, obj> * Action * int =
            if evalSteps = 0 then
                (eff, RescheduleRun, 0)
            else
                match eff with
                | NonBlocking action ->
                    match action() with
                    | Ok res -> (Success res, Finished, evalSteps - 1)
                    | Error err -> (Failure err, Finished, evalSteps - 1)
                | Blocking chan ->
                    if prevAction = RescheduleBlock (BlockingChannel chan) then
                        (Success <| chan.Take(), Finished, evalSteps - 1)
                    else
                        (Blocking chan, RescheduleBlock (BlockingChannel chan), evalSteps)
                | Send (value, chan) ->
                    chan.Add value
                    dataEventQueue.Add <| BlockingChannel chan
                    (Success value, Finished, evalSteps - 1)
                | Concurrent (eff, fiber, llfiber) ->
                    workItemQueue.Add <| WorkItem.Create(eff, llfiber, prevAction)
                    userFibersMap.TryAdd (llfiber, ()) |> ignore
                    (Success fiber, Finished, evalSteps - 1)
                | AwaitFiber llfiber ->
                    if prevAction = RescheduleBlock (BlockingFiber llfiber) then
                        match llfiber.Await() with
                        | Ok res -> (Success res, Finished, evalSteps - 1)
                        | Error err -> (Failure err, Finished, evalSteps - 1)
                    else
                        (AwaitFiber llfiber, RescheduleBlock (BlockingFiber llfiber), evalSteps)
                | Sequence (eff, cont) ->
                    match this.LowLevelEval eff prevAction evalSteps with
                    | Success res, Finished, evalSteps -> this.LowLevelEval (cont res) Finished evalSteps
                    | Failure err, Finished, evalSteps -> (Failure err, Finished, evalSteps)
                    | eff, action, evalSteps -> (Sequence (eff, cont), action, evalSteps)
                | SequenceError (eff, cont) ->
                    match this.LowLevelEval eff prevAction evalSteps with
                    | Success res, Finished, evalSteps -> (Success res, Finished, evalSteps)
                    | Failure err, Finished, evalSteps -> this.LowLevelEval (cont err) Finished evalSteps
                    | eff, action, evalSteps -> (Sequence (eff, cont), action, evalSteps)
                | Success res ->
                    (Success res, Finished, evalSteps - 1)
                | Failure err ->
                    (Failure err, Finished, evalSteps - 1)

        override _.Eval<'R, 'E>(eff: FIO<'R, 'E>) : Fiber<'R, 'E> =
            
            printfn $"\n\n\n\nworkItemQueue length before interpreting: %i{workItemQueue.Count}"
            printfn $"blockingDict length before interpreting: %i{blockingWorkItemMap.Count}"
            printfn $"dataEventQueue length before interpreting: %i{dataEventQueue.Count}"
            printfn $"userFibersList length before interpreting: %i{userFibersMap.Count}"
            
            let fiber = Fiber<'R, 'E>()
            workItemQueue.Add <| WorkItem.Create(eff.Upcast(), fiber.ToLowLevel(), None)
            let _ = fiber.Await()
            
            printfn $"\nworkItemQueue length after interpreting: %i{workItemQueue.Count}"
            printfn $"blockingDict length after interpreting: %i{blockingWorkItemMap.Count}"
            printfn $"dataEventQueue length after interpreting: %i{dataEventQueue.Count}"
            printfn $"userFibersList length after interpreting: %i{userFibersMap.Count}"
            
            fiber

        member private this.CreateWorkers() =
            let createEvalWorkers evalSteps startId endId =
                List.map (fun id ->
                    EvalWorker(id.ToString(), self, workItemQueue, blockingWorkItemMap,
                    dataEventQueue, userFibersMap, evalSteps)) [startId..endId]
               
            let createBlockingWorkers startId endId =
                List.map (fun id ->
                    BlockingWorker(id.ToString(), workItemQueue, blockingWorkItemMap, dataEventQueue))
                    [startId..endId]

            let (evalWorkerCount, blockingWorkerCount, evalSteps) = this.GetConfiguration()
            createEvalWorkers evalSteps 0 (evalWorkerCount - 1) |> ignore
            createBlockingWorkers evalWorkerCount (evalWorkerCount + blockingWorkerCount - 1) |> ignore

        member _.GetConfiguration() =
            let evalWorkerCount =
                match evalWorkerCount with
                | Some evalWorkerCount -> evalWorkerCount
                | _ -> defaultEvalWorkerCount

            let blockingWorkerCount =
                match blockingWorkerCount with
                | Some blockingWorkerCount -> blockingWorkerCount
                | _ -> defaultBlockingWorkerCount

            let evalSteps =
                match evalSteps with
                | Some evalSteps -> evalSteps
                | _ -> defaultEvalSteps

            (evalWorkerCount, blockingWorkerCount, evalSteps)
            