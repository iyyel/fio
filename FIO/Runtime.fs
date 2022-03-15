// FIO - effectful programming library for F#
// Copyright (c) 2022, Daniel Larsen and Technical University of Denmark (DTU)
// All rights reserved.

namespace FSharp.FIO

open FSharp.FIO.FIO

open System.Threading.Tasks
open System.Collections.Concurrent

module Runtime =

    type [<AbstractClass>] Runtime() = class end

(***********************************************)
(*                                             *)
(* Naive runtime                               *)
(*                                             *)
(***********************************************)
    type Naive() =
        inherit Runtime()

        member internal this.LowLevelEval (eff : FIO<obj, obj>) : Result<obj, obj> =
            match eff with
            | NonBlocking action               -> action()
            | Blocking chan                    -> Ok <| chan.Take()
            | Concurrent (eff, fiber, llfiber) -> let _ = Task.Factory.StartNew(fun () -> llfiber.Complete <| this.LowLevelEval eff)
                                                  Ok fiber
            | Await llfiber                    -> llfiber.Await()
            | Sequence (eff, cont)             -> match this.LowLevelEval eff with
                                                  | Ok res    -> this.LowLevelEval <| cont res
                                                  | Error err -> Error err
            | Success res                      -> Ok res
            | Failure err                      -> Error err

        member this.Eval (eff : FIO<'R, 'E>) : Fiber<'R, 'E> =
            let fiber = new Fiber<'R, 'E>()
            let _ = Task.Factory.StartNew(fun () -> fiber.ToLowLevel().Complete <| this.LowLevelEval (eff.Upcast()))
            fiber

(***********************************************)
(*                                             *)
(* Advanced runtime                            *)
(*                                             *)
(***********************************************)
    type WorkItem = { Eff: FIO<obj, obj>; Chan: BlockingCollection<Result<obj, obj>> } with
        static member Create(eff) = { Eff = eff; Chan = new BlockingCollection<Result<obj, obj>>() }
        static member Create(eff, chan) = { Eff = eff; Chan = chan }
        member internal this.Await() = this.Chan.Take()
        member internal this.Completed() = this.Chan.Count > 0

    and Worker(id, execSteps, runtime : Advanced, workQueue : BlockingCollection<WorkItem>) =
        let _ = Task.Factory.StartNew(fun () ->
            for workItem in workQueue.GetConsumingEnumerable() do
                #if DEBUG
                printfn $"DEBUG: Worker(%s{id}): Found new work. Evaluating..."
                #endif
                match runtime.LowLevelEval workItem.Eff execSteps with
                | Rest eff                ->
                                            #if DEBUG
                                            printfn $"DEBUG: Worker(%s{id}): Evaluated %i{execSteps} steps on effect. Adding rest to queue."
                                            #endif
                                            workQueue.Add <| WorkItem.Create eff
                | Result (res, execSteps) ->
                                            #if DEBUG
                                            printfn $"DEBUG: Worker(%s{id}): Got result (%A{res}) with (%i{execSteps}) steps left. Adding result to channel."
                                            #endif
                                            workItem.Chan.Add res
                | Done                    ->
                                            #if DEBUG
                                            printfn $"DEBUG: Worker(%s{id}): Done."
                                            #endif
                                            ())
        member _.Id = id

    and EvalResult =
        | Rest of effect: FIO<obj, obj>
        | Result of result: Result<obj, obj> * execStepsLeft: int
        | Done

    and Advanced(?workerCount, ?execSteps) as self =
        inherit Runtime()
        let defaultExecSteps = 10

        let createWorkers workQueue =
            let create count = List.map (fun id -> 
                match execSteps with
                | Some execSteps -> Worker(id.ToString(), execSteps, self, workQueue)
                | _              -> Worker(id.ToString(), defaultExecSteps, self, workQueue)) [0..count - 1]
            match workerCount with
            | Some count -> create count
            | _          -> create System.Environment.ProcessorCount
        
        let workQueue = new BlockingCollection<WorkItem>()
        let _ = createWorkers workQueue

        member internal this.LowLevelEval (eff : FIO<obj, obj>) (execSteps : int) : EvalResult =
            if execSteps = 0 then
                Rest eff
            else 
                match eff with
                | NonBlocking action               -> Result <| (action(), execSteps)
                | Blocking chan                    -> Result <| (Ok <| chan.Take(), execSteps)
                | Concurrent (eff, fiber, llfiber) -> let workItem = WorkItem.Create eff
                                                      workQueue.Add workItem
                                                      let _ = Task.Factory.StartNew(fun () -> llfiber.Complete <| workItem.Chan.Take())
                                                      Result <| (Ok fiber, execSteps)
                | Await llfiber                    -> Result <| (llfiber.Await(), execSteps)
                | Sequence (eff, cont)             -> match this.LowLevelEval eff (execSteps - 1) with
                                                      | Rest eff                -> workQueue.Add <| WorkItem.Create eff
                                                                                   Done
                                                      | Result (res, execSteps) ->
                                                          match res with
                                                          | Ok res    -> this.LowLevelEval (cont res) (execSteps - 1)
                                                          | Error err -> Result <| (Error err, execSteps)
                                                      | Done       -> Done
                | Success res                      -> Result <| (Ok res, execSteps)
                | Failure err                      -> Result <| (Error err, execSteps)

        member _.Eval (eff : FIO<'R, 'E>) : Fiber<'R, 'E> =
            let workItem = WorkItem.Create <| eff.Upcast()
            workQueue.Add workItem
            let fiber = Fiber<'R, 'E>()
            let _ = Task.Factory.StartNew(fun () -> fiber.ToLowLevel().Complete <| workItem.Chan.Take())
            fiber
