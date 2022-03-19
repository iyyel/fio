// FIO - effectful programming library for F#
// Copyright (c) 2022, Daniel Larsen and Technical University of Denmark (DTU)
// All rights reserved.

module ArgParser

open Argu

type Arguments =
    | Naive
    | Advanced of evalworkercount: int * blockingworkercount: int * evalsteps: int
    | Pingpong of roundcount: int
    | ThreadRing of processcount: int * roundcount: int
    | Big of processcount: int * roundcount: int
    | Bang of processcount: int * roundcount: int
    | [<Mandatory>] Runs of runs: int

    interface IArgParserTemplate with
        member args.Usage =
            match args with
            | Naive _      -> "specify naive runtime"
            | Advanced _   -> "specify evalworkercount, blockingworkercount and evalsteps for advanced runtime"
            | Pingpong _   -> "specify round count for pingpong benchmark"
            | ThreadRing _ -> "specify process count and round count for threadring benchmark"
            | Big _        -> "specify process count and round count for big benchmark"
            | Bang _       -> "specify process count and round count for bang benchmark"
            | Runs _       -> "specify the number of runs for each benchmark"

type Parser() =
    let parser = ArgumentParser.Create<Arguments>()

    member _.GetResults(args) =
        let results = parser.Parse args
        results
