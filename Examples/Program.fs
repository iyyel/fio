module Program

open FSharp.FIO
open Examples

[<EntryPoint>]
let main _ =

    let chanInt = Channel<int>()
    let chanStr = Channel<string>()

    let result = NaiveEval(Ring.processRing 3 1)
    printfn $"Result: %A{result}"

    0