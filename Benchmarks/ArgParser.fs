(**********************************************************************************)
(* FIO - Effectful programming library for F#                                     *)
(* Copyright (c) 2022, Daniel Larsen and Technical University of Denmark (DTU)    *)
(* All rights reserved                                                            *)
(**********************************************************************************)

module ArgParser

open Argu

type Arguments =
    | Naive_Runtime
    | Advanced_Runtime of evalworkercount: int * evalsteps: int
    | Pingpong of roundcount: int
    | ThreadRing of processcount: int * roundcount: int
    | Big of processcount: int * roundcount: int
    | Bang of processcount: int * roundcount: int
    | ReverseBang of processcount: int * roundcount: int
    | [<Mandatory>] Runs of runs: int
    | [<Mandatory>] Process_Increment of processcountinc: int * inctimes: int

    interface IArgParserTemplate with
        member this.Usage =
            match this with
            | Naive_Runtime _ -> 
                "specify naive runtime. (specify only one runtime)"
            | Advanced_Runtime _ ->
                "specify eval worker count, blocking worker count and eval steps for advanced runtime. (specify only one runtime)"
            | Pingpong _ ->
                "specify round count for pingpong benchmark."
            | ThreadRing _ ->
                "specify process count and round count for threadring benchmark."
            | Big _ ->
                "specify process count and round count for big benchmark."
            | Bang _ ->
                "specify process count and round count for bang benchmark."
            | ReverseBang _ ->
                "specify process count and round count for reversebang benchmark."
            | Runs _ ->
                "specify the number of runs for each benchmark."
            | Process_Increment _ ->
                "specify the value of process count increment and how many times."

type Parser() =
    let parser = ArgumentParser.Create<Arguments>()

    member _.GetResults args =
        let results = parser.Parse args
        results
