open FSharp.FIO

let e1 = Output(42)

let e2 = Input(3, (fun value -> e1))
