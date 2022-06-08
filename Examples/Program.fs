(**********************************************************************************)
(* FIO - A type-safe, highly concurrent programming library for F#                *)
(* Copyright (c) 2022, Daniel Larsen and Technical University of Denmark (DTU)    *)
(* All rights reserved                                                            *)
(**********************************************************************************)

module Program

open System.Threading

ThreadPool.SetMaxThreads(32767, 32767) |> ignore
ThreadPool.SetMinThreads(32767, 32767) |> ignore

[<EntryPoint>]
let main _ =
    Examples.errorHandlingExample()
    0