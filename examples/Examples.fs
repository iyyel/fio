open FSharp.FIO

let c = FIO.Channel<int>()

let p1 c =
    let x = 0
    FIO.Send(x, c, fun () ->
        printfn "p1 sent: %i" x
        FIO.Receive(c, fun y -> 
            printfn "p1 received: %i" y
            FIO.Send(y, c, fun () ->
                printfn "p1 sent: %i" y
                FIO.Receive(c, fun z -> 
                    printfn "p1 received: %i" z
                    FIO.Return z))))

let p2 c =
    FIO.Receive(c, fun x -> 
        printfn "p2 received: %i" x
        let y = x + 10
        FIO.Send(y, c, fun () -> 
            printfn "p2 sent: %i" y
            FIO.Receive(c, fun z -> 
                printfn "p2 received: %i" z
                let v = z + 10
                FIO.Send(v, c, fun () -> 
                    printfn "p2 sent: %i" v
                    FIO.Return v))))

[<EntryPoint>]
let main argv =
    
    let result = FIO.NaiveEval(FIO.Parallel(p1 c, p2 c))
    printfn "Result: %i" result

    0