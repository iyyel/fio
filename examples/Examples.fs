open FSharp.FIO
open Pingpong
open Ring
 
[<EntryPoint>]
let main _ =

    let chanInt = FIO.Channel<int>()
    let chanStr = FIO.Channel<string>()


    let result = FIO.NaiveEval(intStrPingpongInf chanInt chanStr)
    printfn $"Result: %A{result}"
    
    0