open FSharp.FIO
open Pingpong
open Ring
 
[<EntryPoint>]
let main _ =

    let chanInt = FIO.Channel<int>()
    let chanStr = FIO.Channel<string>()

    let result = FIO.NaiveEval(Ring.processRing 20 2)
    printfn $"Result: %A{result}"
    
    0