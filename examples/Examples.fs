open FSharp.FIO
open Pingpong
open Ring
 
[<EntryPoint>]
let main _ =

    let chanInt = FIO.Channel<int>()
    let chanStr = FIO.Channel<string>()

    //let result = FIO.NaiveEval()
    //printfn $"Result: %A{result}"
    
    Ring.run 3 1

    0