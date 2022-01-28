module Program

open FSharp.FIO
open Examples

[<EntryPoint>]
let main _ =

    let chanInt = Channel<int>()
    let chanStr = Channel<string>()

    let result = NaiveEval(Ring.processRing 10 1000)
    printfn $"Result: %A{result}"

    0