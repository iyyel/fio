﻿// FIO - effectful programming library for F#
// Copyright (c) 2022, Daniel Larsen and Technical University of Denmark (DTU)
// All rights reserved.

module ArgParser

open Argu

type Runtime =
    | Naive
    | Default
    | Optimized

type Arguments =
    | [<Mandatory>] Runtime of runtime:Runtime
    | Pingpong of RoundCount:int
    | ThreadRing of ProcessCount:int * RoundCount:int
    | Big of ProcessCount:int * RoundCount:int
    | Bang of ProcessCount:int * RoundCount:int
    | [<Mandatory>] Runs of rounds:int

    interface IArgParserTemplate with
        member args.Usage =
            match args with
            | Runtime _    -> "specify desired runtime (default, naive, optimized)"
            | Pingpong _   -> "specify rounds for pingpong benchmark"
            | ThreadRing _ -> "specify process count and round count for threadring benchmark"
            | Big _        -> "specify process count and round count for big benchmark"
            | Bang _       -> "specify process count and round count for bang benchmark"
            | Runs _       -> "specify the amount of runs for each benchmark"

type Parser() =
    let parser = ArgumentParser.Create<Arguments>()

    member _.GetResults(args) =
        let results = parser.Parse args
        results