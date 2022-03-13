// FIO - effectful programming library for F#
// Copyright (c) 2022, Daniel Larsen and Technical University of Denmark (DTU)
// All rights reserved.

namespace FSharp.FIO

open FSharp.FIO.FIO

module Runtime =

    type [<AbstractClass>] Runtime() = class end

    and Naive() =
        inherit Runtime()

        member this.Run (fio : FIO<'R, 'E>) : Fiber<'R, 'E> =
            new Fiber<'R, 'E>(fio, this.Eval)

        member private this.LowLevelEval (fio : FIO<obj, obj>) : Result<obj, obj> =
            match fio with
            | NonBlocking action     -> action()
            | Blocking chan          -> Ok <| chan.Take()
            | Concurrent (fio, cont) -> let fiber = Fiber<_, _>(fio, this.LowLevelEval)
                                        this.LowLevelEval <| cont fiber
            | Await (fiber, cont)    -> this.LowLevelEval <| (cont <| fiber.Await())
            | Sequence (fio, cont)   -> match this.LowLevelEval fio with
                                        | Ok res    -> this.LowLevelEval <| cont res
                                        | Error err -> Error err
            | Success res -> Ok res
            | Failure err -> Error err

        member private this.Eval (fio : FIO<'R, 'E>) : Result<'R, 'E> =
            match this.LowLevelEval <| upcastBoth fio with
            | Ok res    -> Ok (res :?> 'R)
            | Error err -> Error (err :?> 'E)

    and Advanced() =
        inherit Naive()

    and Default() =
        inherit Naive()
