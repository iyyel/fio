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
            | DataEvent chan ->
                let res = chan.Take()
                chan.Add res
                Ok res
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
        | Evaluated

    and WorkItem = { Eff: FIO<obj, obj>; LLFiber: LowLevelFiber }
                   static member Create(eff, llfiber) = { Eff = eff; LLFiber = llfiber }
                   
    and EvalWorker(
            id,
            runtime: Advanced,
            workQueue: BlockingCollection<WorkItem>,
            blockingDict: ConcurrentDictionary<Blocker, WorkItem>,
            blockingEventQueue: BlockingCollection<Blocker>,
            evalSteps) =
        let _ = (async {
            for workItem in workQueue.GetConsumingEnumerable() do
                #if DEBUG
                printfn $"DEBUG: EvalWorker(%s{id}): Found new work. Executing LowLevelEval..."
                #endif
                match runtime.LowLevelEval workItem.Eff evalSteps blockingEventQueue with
                | Success res, Evaluated, evalSteps ->
                    #if DEBUG
                    printfn $"DEBUG: EvalWorker(%s{id}): Got success result (%A{res}) with (%i{evalSteps}) steps left. Completing work item."
                    #endif
                    workItem.LLFiber.Complete <| Ok res
                    blockingEventQueue.Add <| BlockingFiber workItem.LLFiber
                | Failure err, Evaluated, evalSteps ->
                    #if DEBUG
                    printfn $"DEBUG: EvalWorker(%s{id}): Got error result (%A{err}) with (%i{evalSteps}) steps left. Completing work item."
                    #endif
                    workItem.LLFiber.Complete <| Error err
                    blockingEventQueue.Add <| BlockingFiber workItem.LLFiber
                | eff, RescheduleRun, _ ->
                    #if DEBUG
                    printfn $"DEBUG: EvalWorker(%s{id}): Finished evaluation steps. Rescheduling rest of effect."
                    #endif
                    workQueue.Add <| WorkItem.Create(eff, workItem.LLFiber)
                | eff, RescheduleBlock blocker, _ ->
                    #if DEBUG
                    printfn $"DEBUG: EvalWorker(%s{id}): Got blocking effect. Rescheduling to blocking dictionary."
                    #endif
                    match blockingDict.TryAdd (blocker, WorkItem.Create(eff, workItem.LLFiber)) with
                    | true ->
                        #if DEBUG
                        printfn $"DEBUG: EvalWorker(%s{id}): Added blocker to dictionary successfully!"
                        #endif
                        ()
                    | _ -> 
                        #if DEBUG
                        printfn $"DEBUG: EvalWorker(%s{id}): Failed to add blocker to dictionary!"
                        #endif
                        ()
                | _ -> failwith $"EvalWorker(%s{id}): Error occurred while evaluating effect!"
            } |> Async.StartAsTask |> ignore)
        
    and BlockingWorker(
            id,
            workQueue: BlockingCollection<WorkItem>,
            blockingDict: ConcurrentDictionary<Blocker, WorkItem>,
            blockingEventQueue: BlockingCollection<Blocker>) =
        let _ = (async {
            for blocker in blockingEventQueue.GetConsumingEnumerable() do
                #if DEBUG
                printfn $"DEBUG: BlockingWorker(%s{id}): Got new blocking event!"
                #endif
                match blockingDict.TryRemove blocker with
                | true, workItem -> 
                    //#if DEBUG
                    printfn $"DEBUG: BlockingWorker(%s{id}): The blocking channel or fiber was in the dictionary. Adding back work item."
                    //#endif
                    workQueue.Add workItem
                | false, _ -> 
                    //#if DEBUG
                    //printfn $"DEBUG: BlockingWorker(%s{id}): The blocking channel or fiber wasn't in the blocking dictionary yet. Adding back."
                    //#endif
                    blockingEventQueue.Add blocker
             } |> Async.StartAsTask |> ignore)

    and Advanced(
            ?evalWorkerCount,
            ?blockingWorkerCount,
            ?evalSteps) as self =
        inherit Runtime()
        let defaultEvalWorkerCount = System.Environment.ProcessorCount / 2
        let defaultBlockingWorkerCount = System.Environment.ProcessorCount / 2
        let defaultEvalSteps = 10
        let workItemQueue = new BlockingCollection<WorkItem>()
        let blockingDict = ConcurrentDictionary<Blocker, WorkItem>()
        let blockingEventQueue = new BlockingCollection<Blocker>()
        do self.CreateWorkers()

        member internal this.LowLevelEval
                (eff: FIO<obj, obj>)
                (evalSteps: int)
                (blockingEventQueue: BlockingCollection<Blocker>)
                : FIO<obj, obj> * Action * int =
            if evalSteps = 0 then
                (eff, RescheduleRun, 0)
            else
                match eff with
                | NonBlocking action ->
                    match action() with
                    | Ok res -> (Success res, Evaluated, evalSteps - 1)
                    | Error err -> (Failure err, Evaluated, evalSteps - 1)
                | Blocking chan ->
                    if chan.Count() > 0 then
                        (Success <| chan.Take(), Evaluated, evalSteps - 1)
                    else
                        (Blocking chan, RescheduleBlock (BlockingChannel chan), evalSteps)
                | Concurrent (eff, fiber, llfiber) ->
                    workItemQueue.Add <| WorkItem.Create(eff, llfiber)
                    (Success fiber, Evaluated, evalSteps - 1)
                | AwaitFiber llfiber ->
                    if llfiber.Completed() then
                        match llfiber.Await() with
                        | Ok res -> (Success res, Evaluated, evalSteps - 1)
                        | Error err -> (Failure err, Evaluated, evalSteps - 1)
                    else
                        (AwaitFiber llfiber, RescheduleBlock (BlockingFiber llfiber), evalSteps)
                | Sequence (eff, cont) ->
                    match this.LowLevelEval eff evalSteps blockingEventQueue with
                    | Success res, Evaluated, evalSteps -> this.LowLevelEval (cont res) evalSteps blockingEventQueue
                    | Failure err, Evaluated, evalSteps -> (Failure err, Evaluated, evalSteps)
                    | eff, action, evalSteps -> (Sequence (eff, cont), action, evalSteps)
                | SequenceError (eff, cont) ->
                    match this.LowLevelEval eff evalSteps blockingEventQueue with
                    | Success res, Evaluated, evalSteps -> this.LowLevelEval (cont res) evalSteps blockingEventQueue
                    | Failure err, Evaluated, evalSteps -> (Failure err, Evaluated, evalSteps)
                    | eff, action, evalSteps -> (Sequence (eff, cont), action, evalSteps)
                | DataEvent chan -> 
                    blockingEventQueue.Add <| BlockingChannel chan
                    let res = chan.Take()
                    chan.Add res
                    (Success res, Evaluated, evalSteps - 1)
                | Success res ->
                    (Success res, Evaluated, evalSteps - 1)
                | Failure err ->
                    (Failure err, Evaluated, evalSteps - 1)

        override _.Eval<'R, 'E>(eff: FIO<'R, 'E>) : Fiber<'R, 'E> =
            let fiber = Fiber<'R, 'E>()
            workItemQueue.Add <| WorkItem.Create(eff.Upcast(), fiber.ToLowLevel())
            fiber
            
        member private _.CreateWorkers() =
            let createEvalWorkers startId endId =
                match evalSteps with
                | Some steps -> 
                    List.map (fun id -> 
                        EvalWorker(id.ToString(), self, workItemQueue, blockingDict, blockingEventQueue, steps))
                        [startId..endId]
                | _ -> 
                    List.map (fun id ->
                        EvalWorker(id.ToString(), self, workItemQueue, blockingDict, blockingEventQueue, defaultEvalSteps))
                        [startId..endId]

            let createBlockingWorkers startId endId =
                List.map (fun id -> 
                    BlockingWorker(id.ToString(), workItemQueue, blockingDict, blockingEventQueue))
                    [startId..endId]

            match evalWorkerCount with
            | Some evalWorkerCount ->
                createEvalWorkers 0 (evalWorkerCount - 1) |> ignore
                match blockingWorkerCount with
                | Some blockingWorkerCount ->
                    createBlockingWorkers evalWorkerCount (evalWorkerCount + blockingWorkerCount - 1)
                    |> ignore
                | _ ->
                    createBlockingWorkers evalWorkerCount (evalWorkerCount + defaultBlockingWorkerCount - 1)
                    |> ignore
            | _ -> createEvalWorkers 0 (defaultEvalWorkerCount - 1) |> ignore

            match blockingWorkerCount with
            | Some blockingWorkerCount ->
                createBlockingWorkers defaultEvalWorkerCount (defaultEvalWorkerCount + blockingWorkerCount - 1)
                |> ignore
            | _ ->
                createBlockingWorkers defaultEvalWorkerCount (defaultEvalWorkerCount + defaultBlockingWorkerCount - 1)
                |> ignore

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
            