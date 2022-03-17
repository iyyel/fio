// FIO - effectful programming library for F#
// Copyright (c) 2022, Daniel Larsen and Technical University of Denmark (DTU)
// All rights reserved.

namespace FSharp.FIO

open FSharp.FIO.FIO

open System.Collections.Concurrent

module Runtime =

    type [<AbstractClass>] Runtime() = class end

(***************************************************************************)
(*                                                                         *)
(*                              Naive runtime                              *)
(*                                                                         *)
(***************************************************************************)
    type Naive() =
        inherit Runtime()

        member internal this.LowLevelEval (eff : FIO<obj, obj>) : Result<obj, obj> =
            match eff with
            | NonBlocking action               -> action()
            | Blocking chan                    -> Ok <| chan.Take()
            | Concurrent (eff, fiber, llfiber) -> async {
                                                      llfiber.Complete <| this.LowLevelEval eff
                                                  } |> Async.StartAsTask |> ignore
                                                  Ok fiber
            | Await llfiber                    -> llfiber.Await()
            | Sequence (eff, cont)             -> match this.LowLevelEval eff with
                                                  | Ok res    -> this.LowLevelEval <| cont res
                                                  | Error err -> Error err
            | SequenceError (eff, cont)        -> match this.LowLevelEval eff with
                                                  | Ok res    -> Ok res
                                                  | Error err -> this.LowLevelEval <| cont err
            | Success res                      -> Ok res
            | Failure err                      -> Error err

        member this.Eval (eff : FIO<'R, 'E>) : Fiber<'R, 'E> =
            let fiber = new Fiber<'R, 'E>()
            async {
                fiber.ToLowLevel().Complete <| this.LowLevelEval (eff.Upcast())
            } |> Async.StartAsTask |> ignore
            fiber

(***************************************************************************)
(*                                                                         *)
(*                           Advanced runtime                              *)
(*                                                                         *)
(***************************************************************************)
    type WorkItem = { Eff: FIO<obj, obj>; LLFiber: LowLevelFiber } with
        static member Create(eff, llfiber) = { Eff = eff; LLFiber = llfiber }
        member internal this.Complete(res : Result<obj, obj>) = this.LLFiber.Complete res

    and Worker(id, runtime : Advanced, workQueue : BlockingCollection<WorkItem>, execSteps) =
        let _ = (async {
            for workItem in workQueue.GetConsumingEnumerable() do
                #if DEBUG
                printfn $"DEBUG: Worker(%s{id}): Found new work. Executing LowLevelEval..."
                #endif
                match runtime.LowLevelEval workItem.Eff execSteps with
                | (Success res, execSteps) ->
                                              #if DEBUG
                                              printfn $"DEBUG: Worker(%s{id}): Got success result (%A{res}) with (%i{execSteps}) steps left. Completing work item."
                                              #endif
                                              workItem.Complete <| Ok res
                | (Failure err, execSteps) ->
                                              #if DEBUG
                                              printfn $"DEBUG: Worker(%s{id}): Got error result (%A{err}) with (%i{execSteps}) steps left. Completing work item."
                                              #endif
                                              workItem.Complete <| Error err
                | (eff, execSteps)         ->
                                              #if DEBUG
                                              printfn $"DEBUG: Worker(%s{id}): Got effect back with (%i{execSteps}) steps left. Creating new work item."
                                              #endif
                                              workQueue.Add <| WorkItem.Create (eff, workItem.LLFiber)
        } |> Async.StartAsTask |> ignore)

    and Advanced(?workerCount, ?execSteps) as self =
        inherit Runtime()
        let defaultWorkerCount = System.Environment.ProcessorCount
        let defaultExecSteps = 10
        let workQueue = new BlockingCollection<WorkItem>()
        let createWorkers =
            let create count = List.map (fun id ->
                match execSteps with
                | Some execSteps -> Worker(id.ToString(), self, workQueue, execSteps)
                | _              -> Worker(id.ToString(), self, workQueue, defaultExecSteps)) [0..count - 1]
            match workerCount with
            | Some count -> create count
            | _          -> create defaultWorkerCount
        let _ = createWorkers

        member internal this.LowLevelEval (eff : FIO<obj, obj>) (execSteps : int) : FIO<obj, obj> * int =
            if execSteps = 0 then
                (eff, execSteps)
            else
                match eff with
                | NonBlocking action               -> match action() with
                                                      | Ok res    -> (Success res, execSteps - 1)
                                                      | Error err -> (Failure err, execSteps - 1)
                | Blocking chan                    -> (Success <| chan.Take(), execSteps - 1)
                | Concurrent (eff, fiber, llfiber) -> workQueue.Add <| WorkItem.Create (eff, llfiber)
                                                      (Success fiber, execSteps - 1)
                | Await llfiber                    -> match llfiber.Await() with
                                                      | Ok res    -> (Success res, execSteps - 1)
                                                      | Error err -> (Failure err, execSteps - 1)
                | Sequence (eff, cont)             -> match this.LowLevelEval eff (execSteps) with
                                                      | (Success res, execSteps) -> this.LowLevelEval (cont res) execSteps
                                                      | (Failure err, _)         -> (Failure err, 0)
                                                      | (eff, _)                 -> (Sequence (eff, cont), execSteps)
                | SequenceError (eff, cont)        -> match this.LowLevelEval eff (execSteps) with
                                                      | (Success res, _)         -> (Success res, 0)
                                                      | (Failure err, execSteps) -> this.LowLevelEval (cont err) execSteps
                                                      | (eff, _)                 -> (Sequence (eff, cont), execSteps)
                | Success res                      -> (Success res, 0)
                | Failure err                      -> (Failure err, 0)

        member _.Eval (eff : FIO<'R, 'E>) : Fiber<'R, 'E> =
            let fiber = Fiber<'R, 'E>()
            workQueue.Add <| WorkItem.Create (eff.Upcast(), fiber.ToLowLevel())
            fiber
