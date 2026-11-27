use std::env;
use std::io::{self, Write};
use std::net::{Shutdown, SocketAddr, TcpListener, TcpStream};

fn main() -> Result<(), Box<dyn std::error::Error>> {
    let args: Vec<_> = env::args().collect();
    let (mode, address) = match args.as_slice() {
        [_, mode, address] => (mode.as_str(), address.parse::<SocketAddr>()?),
        _ => return Err("usage: stream send|receive LOOPBACK_IP:PORT".into()),
    };
    if !address.ip().is_loopback() {
        return Err("this experimental transport accepts loopback addresses only".into());
    }
    match mode {
        "receive" => {
            let listener = TcpListener::bind(address)?;
            eprintln!("{}", listener.local_addr()?);
            let (mut socket, _) = listener.accept()?;
            let mut output = io::stdout().lock();
            io::copy(&mut socket, &mut output)?;
            output.flush()?;
        }
        "send" => {
            let mut socket = TcpStream::connect(address)?;
            socket.set_nodelay(true)?;
            io::copy(&mut io::stdin().lock(), &mut socket)?;
            socket.shutdown(Shutdown::Write)?;
        }
        _ => return Err("unknown mode".into()),
    }
    Ok(())
}
