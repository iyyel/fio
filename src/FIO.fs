namespace FSharp.FIO

type Effect<'a> =
    | Input of 'a * ('a -> Effect<'a>)
    | Output of 'a
