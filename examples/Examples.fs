open FSharp.FIO

let c = FIO.Channel<int>()

let p1 c =
    let x = 0
    FIO.send(x, c, fun () ->
        printfn "p1 sent: %i" x
        FIO.receive(c, fun y -> 
            printfn "p1 received: %i" y
            FIO.send(y, c, fun () ->
                printfn "p1 sent: %i" y
                FIO.receive(c, fun z -> 
                    printfn "p1 received: %i" z
                    FIO.Return z))))

let p2 c =
    FIO.receive(c, fun x -> 
        printfn "p2 received: %i" x
        let y = x + 10
        FIO.send(y, c, fun () -> 
            printfn "p2 sent: %i" y
            FIO.receive(c, fun z -> 
                printfn "p2 received: %i" z
                let v = z + 10
                FIO.send(v, c, fun () -> 
                    printfn "p2 sent: %i" v
                    FIO.Return v))))

[<EntryPoint>]
let main argv =
    
    let result = FIO.naiveEval(FIO.Parallel(p1 c, p2 c))
    printfn "Result: %i" result

    0