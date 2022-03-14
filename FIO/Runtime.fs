// FIO - effectful programming library for F#
// Copyright (c) 2022, Daniel Larsen and Technical University of Denmark (DTU)
// All rights reserved.

namespace FSharp.FIO

open FSharp.FIO.FIO
open System.Threading.Tasks

module Runtime =

    type [<AbstractClass>] Runtime() =
        abstract Eval : FIO<'R, 'E> -> Fiber<'R, 'E>
        
    type Naive() =
        inherit Runtime()

        member private this.LowLevelEval (fio : FIO<obj, obj>) : Result<obj, obj> =
            match fio with
            | NonBlocking action               -> action()
            | Blocking chan                    -> Ok <| chan.Take()
            | Concurrent (fio, fiber, llfiber) -> let _ = Task.Factory.StartNew(fun () -> llfiber.Complete <| this.LowLevelEval fio)
                                                  Ok fiber
            | Await llfiber                    -> llfiber.Await()
            | Sequence (fio, cont)             -> match this.LowLevelEval fio with
                                                  | Ok res    -> this.LowLevelEval <| cont res
                                                  | Error err -> Error err
            | Success res -> Ok res
            | Failure err -> Error err

        override this.Eval (fio : FIO<'R, 'E>) : Fiber<'R, 'E> =
            let fiber = new Fiber<'R, 'E>()
            let _ = Task.Factory.StartNew(fun () -> fiber.ToLowLevel().Complete <| this.LowLevelEval (fio.Upcast()))
            fiber

    type Advanced() =
        inherit Runtime()

        member private this.LowLevelEval (fio : FIO<obj, obj>) : Result<obj, obj> =
            match fio with
            | NonBlocking action               -> action()
            | Blocking chan                    -> Ok <| chan.Take()
            | Concurrent (fio, fiber, llfiber) -> let _ = Task.Factory.StartNew(fun () -> llfiber.Complete <| this.LowLevelEval fio)
                                                  Ok fiber
            | Await llfiber                    -> llfiber.Await()
            | Sequence (fio, cont)             -> match this.LowLevelEval fio with
                                                  | Ok res    -> this.LowLevelEval <| cont res
                                                  | Error err -> Error err
            | Success res -> Ok res
            | Failure err -> Error err

        override this.Eval (fio : FIO<'R, 'E>) : Fiber<'R, 'E> =
            let fiber = new Fiber<'R, 'E>()
            let _ = Task.Factory.StartNew(fun () -> fiber.ToLowLevel().Complete <| this.LowLevelEval (fio.Upcast()))
            fiber
