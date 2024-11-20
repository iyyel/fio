(**********************************************************************************)
(* FIO - A type-safe, highly concurrent programming library for F#                *)
(* Copyright (c) 2025, Daniel Larsen and Technical University of Denmark (DTU)    *)
(* All rights reserved                                                            *)
(**********************************************************************************)

module FIO.Runtime.Core

open FIO.Core

#if DETECT_DEADLOCK || MONITOR
open FIO.Monitor
#endif

open System.Collections.Concurrent

[<AbstractClass>]
type Runner() =
    abstract Run<'R, 'E> : FIO<'R, 'E> -> Fiber<'R, 'E>
