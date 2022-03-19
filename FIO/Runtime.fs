// FIO - effectful programming library for F#
// Copyright (c) 2022, Daniel Larsen and Technical University of Denmark (DTU)
// All rights reserved.

namespace FSharp.FIO

open FSharp.FIO.FIO

open System.Collections.Concurrent

module Runtime =

    [<AbstractClass>]
    type Runtime() =
        abstract Eval<'R, 'E> : FIO<'R, 'E> -> Fiber<'R, 'E>

(***************************************************************************)
(*                                                                         *)
(*                              Naive runtime                              *)
(*                                                                         *)
(***************************************************************************)
    type Naive() =
        inherit Runtime()

        member internal this.LowLevelEval(eff : FIO<obj, obj>) : Result<obj, obj> =
            match eff with
            | NonBlocking action               -> action ()
            | Blocking chan                    -> Ok <| chan.Take()
            | Concurrent (eff, fiber, llfiber) -> async { llfiber.Complete <| this.LowLevelEval eff }
                                                  |> Async.StartAsTask
                                                  |> ignore
                                                  Ok fiber
            | AwaitFiber llfiber               -> llfiber.Await()
            | Sequence (eff, cont)             -> match this.LowLevelEval eff with
                                                  | Ok res    -> this.LowLevelEval <| cont res
                                                  | Error err -> Error err
            | SequenceError (eff, cont)        -> match this.LowLevelEval eff with
                                                  | Ok res    -> Ok res
                                                  | Error err -> this.LowLevelEval <| cont err
            | Success res                      -> Ok res
            | Failure err                      -> Error err

        override this.Eval<'R, 'E>(eff: FIO<'R, 'E>) : Fiber<'R, 'E> =
            let fiber = new Fiber<'R, 'E>()
            async { fiber.ToLowLevel().Complete <| this.LowLevelEval(eff.Upcast()) }
            |> Async.StartAsTask
            |> ignore
            fiber

(***************************************************************************)
(*                                                                         *)
(*                           Advanced runtime                              *)
(*                                                                         *)
(***************************************************************************)
    type Blocker =
        | BlockingChannel of Channel<obj>
        | BlockingFiber of LowLevelFiber

    and Action =
        | RescheduleRun
        | RescheduleBlock of Blocker
        | Evaluated

    and WorkItem = { Eff: FIO<obj, obj>; LLFiber: LowLevelFiber }
                   static member Create(eff, llfiber) = { Eff = eff; LLFiber = llfiber }
                   member internal this.Complete(res: Result<obj, obj>) = this.LLFiber.Complete res
                   
    and BlockingItem = { Blocker: Blocker; WorkItem: WorkItem }
                       static member Create(blocker, workItem) = { Blocker = blocker; WorkItem = workItem }

    and EvalWorker(
            id,
            runtime: Advanced,
            workQueue: BlockingCollection<WorkItem>,
            blockingQueue: BlockingCollection<BlockingItem>,
            execSteps) =
        let _ = (async {
                    for workItem in workQueue.GetConsumingEnumerable() do
                        #if DEBUG
                        printfn $"DEBUG: EvalWorker(%s{id}): Found new work. Executing LowLevelEval..."
                        #endif
                        match runtime.LowLevelEval workItem.Eff execSteps with
                        | Success res, Evaluated, execSteps ->
                            #if DEBUG
                            printfn $"DEBUG: EvalWorker(%s{id}): Got success result (%A{res}) with (%i{execSteps}) steps left. Completing work item."
                            #endif
                            workItem.Complete <| Ok res
                        | Failure err, Evaluated, execSteps ->
                            #if DEBUG
                            printfn $"DEBUG: EvalWorker(%s{id}): Got error result (%A{err}) with (%i{execSteps}) steps left. Completing work item."
                            #endif
                            workItem.Complete <| Error err
                        | eff, RescheduleRun, _ ->
                            #if DEBUG
                            printfn $"DEBUG: EvalWorker(%s{id}): Finished evaluation steps. Rescheduling rest of effect."
                            #endif
                            workQueue.Add <| WorkItem.Create(eff, workItem.LLFiber)
                        | eff, RescheduleBlock blocker, _ ->
                            #if DEBUG
                            printfn $"DEBUG: EvalWorker(%s{id}): Got blocking effect. Rescheduling to blocking queue."
                            #endif
                            blockingQueue.Add <| BlockingItem.Create(blocker, WorkItem.Create(eff, workItem.LLFiber))
                        | _ -> failwith $"EvalWorker(%s{id}): Error occurred while pattern-matching on evaluated effect!"
            } |> Async.StartAsTask |> ignore)

    and BlockingWorker(id,
                       workQueue: BlockingCollection<WorkItem>,
                       blockingQueue: BlockingCollection<BlockingItem>) =
        let _ = (async {
                    for blockingItem in blockingQueue.GetConsumingEnumerable() do
                    #if DEBUG
                    printfn $"DEBUG: BlockingWorker(%s{id}): Got new blocking item! Checking..."
                    #endif
                    match blockingItem.Blocker with
                    | BlockingChannel chan ->
                        if chan.Count() > 0 then
                            #if DEBUG
                            printfn $"DEBUG: BlockingWorker(%s{id}): Channel received data! Adding back work item to work queue."
                            #endif
                            workQueue.Add blockingItem.WorkItem
                        else
                            #if DEBUG
                            printfn $"DEBUG: BlockingWorker(%s{id}): Channel is still blocking. Adding back to blocking queue."
                            #endif
                            blockingQueue.Add blockingItem
                    | BlockingFiber llfiber ->
                        if llfiber.Completed() then
                            #if DEBUG
                            printfn $"DEBUG: BlockingWorker(%s{id}): Fiber received data! Adding back work item to work queue."
                            #endif
                            workQueue.Add blockingItem.WorkItem
                        else
                            #if DEBUG
                            printfn $"DEBUG: BlockingWorker(%s{id}): Fiber is still blocking. Adding back to blocking queue."
                            #endif
                            blockingQueue.Add blockingItem
             } |> Async.StartAsTask |> ignore)

    and Advanced(?evalWorkerCount,
                 ?blockingWorkerCount,
                 ?evalSteps) as self =
        inherit Runtime()
        let defaultEvalWorkerCount = System.Environment.ProcessorCount / 2
        let defaultBlockingWorkerCount = System.Environment.ProcessorCount / 2
        let defaultEvalSteps = 10
        let workQueue = new BlockingCollection<WorkItem>()
        let blockingQueue = new BlockingCollection<BlockingItem>()
        do self.CreateWorkers()

        member internal this.LowLevelEval (eff: FIO<obj, obj>) (evalSteps: int) : FIO<obj, obj> * Action * int =
            if evalSteps = 0 then
                (eff, RescheduleRun, 0)
            else
                match eff with
                | NonBlocking action               -> match action() with
                                                      | Ok res    -> (Success res, Evaluated, evalSteps - 1)
                                                      | Error err -> (Failure err, Evaluated, evalSteps - 1)
                | Blocking chan                    -> if chan.Count() > 0 then
                                                          (Success <| chan.Take(), Evaluated, evalSteps - 1)
                                                      else
                                                          (Blocking chan, RescheduleBlock(BlockingChannel chan), evalSteps)
                | Concurrent (eff, fiber, llfiber) -> workQueue.Add <| WorkItem.Create(eff, llfiber)
                                                      (Success fiber, Evaluated, evalSteps - 1)
                | AwaitFiber llfiber               -> if llfiber.Completed() then
                                                          match llfiber.Await() with
                                                          | Ok res    -> (Success res, Evaluated, evalSteps - 1)
                                                          | Error err -> (Failure err, Evaluated, evalSteps - 1)
                                                      else
                                                          (AwaitFiber llfiber, RescheduleBlock(BlockingFiber llfiber), evalSteps)
                | Sequence (eff, cont)             -> match this.LowLevelEval eff evalSteps with
                                                      | Success res, Evaluated, evalSteps -> this.LowLevelEval(cont res) evalSteps
                                                      | Failure err, Evaluated, evalSteps -> (Failure err, Evaluated, evalSteps)
                                                      | eff, action, evalSteps            -> (Sequence(eff, cont), action, evalSteps)
                | SequenceError (eff, cont)        -> match this.LowLevelEval eff evalSteps with
                                                      | Success res, Evaluated, evalSteps -> this.LowLevelEval(cont res) evalSteps
                                                      | Failure err, Evaluated, evalSteps -> (Failure err, Evaluated, evalSteps)
                                                      | eff, action, evalSteps            -> (Sequence(eff, cont), action, evalSteps)
                | Success res                      -> (Success res, Evaluated, evalSteps - 1)
                | Failure err                      -> (Failure err, Evaluated, evalSteps - 1)

        override _.Eval<'R, 'E>(eff: FIO<'R, 'E>) : Fiber<'R, 'E> =
            let fiber = Fiber<'R, 'E>()
            workQueue.Add <| WorkItem.Create(eff.Upcast(), fiber.ToLowLevel())
            fiber
            
        member private _.CreateWorkers() =
            let createEvalWorkers startId endId =
                match evalSteps with
                | Some steps -> List.map (fun id -> EvalWorker(id.ToString(), self, workQueue, blockingQueue, steps)) [ startId .. endId ]
                | None       -> List.map (fun id -> EvalWorker(id.ToString(), self, workQueue, blockingQueue, defaultEvalSteps)) [ startId .. endId ]

            let createBlockingWorkers startId endId =
                List.map (fun id -> BlockingWorker(id.ToString(), workQueue, blockingQueue)) [ startId .. endId ]

            match evalWorkerCount with
            | Some evalWorkerCount -> createEvalWorkers 0 (evalWorkerCount - 1) |> ignore
                                      match blockingWorkerCount with
                                      | Some blockingWorkerCount -> createBlockingWorkers evalWorkerCount (evalWorkerCount + blockingWorkerCount - 1)
                                                                    |> ignore
                                      | _                        -> createBlockingWorkers evalWorkerCount (evalWorkerCount + defaultBlockingWorkerCount - 1)
                                                                    |> ignore
            | _                    -> createEvalWorkers 0 (defaultEvalWorkerCount - 1) |> ignore

            match blockingWorkerCount with
            | Some blockingWorkerCount -> createBlockingWorkers defaultEvalWorkerCount (defaultEvalWorkerCount + blockingWorkerCount - 1)
                                          |> ignore
            | _                        -> createBlockingWorkers defaultEvalWorkerCount (defaultEvalWorkerCount + defaultBlockingWorkerCount - 1)
                                          |> ignore

        member _.GetConfiguration() =
            let evalWorkerCount = match evalWorkerCount with
                                  | Some evalWorkerCount -> evalWorkerCount
                                  | _ -> defaultEvalWorkerCount

            let blockingWorkerCount = match blockingWorkerCount with
                                      | Some blockingWorkerCount -> blockingWorkerCount
                                      | _ -> defaultBlockingWorkerCount

            let evalSteps = match evalSteps with
                            | Some evalSteps -> evalSteps
                            | _ -> defaultEvalSteps
                            
            (evalWorkerCount, blockingWorkerCount, evalSteps)
            