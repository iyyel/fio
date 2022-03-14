// FIO - effectful programming library for F#
// Copyright (c) 2022, Daniel Larsen and Technical University of Denmark (DTU)
// All rights reserved.

namespace FSharp.FIO

open FSharp.FIO.FIO

module Runtime =

    type [<AbstractClass>] Runtime() = class end

    and Naive() =
        inherit Runtime()

        member private this.LowLevelEval (fio : FIO<obj, obj>) : Result<obj, obj> =
            match fio with
            | NonBlocking action               -> action()
            | Blocking chan                    -> Ok <| chan.Take()
            | Concurrent (fio, fiber, llfiber) -> async { llfiber.Complete <| this.LowLevelEval fio }
                                                      |> Async.StartAsTask |> ignore
                                                  Ok fiber
            | Await llfiber                    -> llfiber.Await()
            | Sequence (fio, cont)             -> match this.LowLevelEval fio with
                                                  | Ok res    -> this.LowLevelEval <| cont res
                                                  | Error err -> Error err
            | Success res -> Ok res
            | Failure err -> Error err

        member this.Eval (fio : FIO<'R, 'E>) : Fiber<'R, 'E> =
            let fiber = new Fiber<'R, 'E>()
            async {
                fiber.ToLowLevel().Complete <| this.LowLevelEval (fio.upcastBoth())
            } |> Async.StartAsTask |> ignore
            fiber

    and Advanced() =
        inherit Naive()

    and Default() =
        inherit Naive()
