# Kills the MongoDB processes started by EphemeralMongo when the .NET process that started them exits, even if it crashes or is killed.
# It's a POSIX shell script, so it runs with the /bin/sh of both Linux (dash, BusyBox, bash) and macOS.
#
# The .NET process starts this script once, then writes one line to its standard input for each event:
#   "<pid>"   a MongoDB process was started, kill it later
#   "-<pid>"  that MongoDB process exited, forget it
# When the .NET process exits for any reason, the operating system closes its end of the standard input,
# so this script reads the end of the stream and kills every process it still remembers.
#
# The optional first argument is the path of a file where this script logs what it does, which tests use to check its behavior.

# Ignores the termination signals, like an empty event handler would. Pressing Ctrl+C in a terminal sends SIGINT to every
# process started from it, including this script, which must outlive the .NET process to clean up after it.
trap '' HUP INT QUIT TERM

# Redirects everything this script and its commands print to the log file, or discards it without one ("${1:-x}" means "$1, or x if empty").
# This way nothing ends up in the console of the .NET process, and this script isn't killed when trying to write to that console once it's gone.
exec >"${1:-/dev/null}" 2>&1

log() {
  printf '%s\n' "$*"
}

log "Watchdog started with PID $$"

# PIDs are recycled: once a MongoDB process exits, an unrelated process can get the same PID.
# To avoid killing the wrong process, the start time of each process is remembered, and must still match before killing it.
#
# Sets the start_time variable to the start time of the process whose PID is the first argument ($1).
# Shell functions can't return strings, hence the variable. The function fails (returns false) if the process doesn't exist.
read_start_time() {
  start_time=
  if [ -d /proc/self ]; then
    # Linux: /proc/<pid>/stat is a single line of space-separated fields, like "1234 (mongod) S 1 ...", and the 22nd one is the start time.
    # The 2nd field is the command name in parentheses, which can contain spaces, so everything up to the last ") " is removed first,
    # leaving the fields from the 3rd one onwards. /proc is used rather than ps, as minimal images like Alpine ship a limited ps.
    read -r stat < "/proc/$1/stat" || return 1

    # "set --" replaces the arguments $1, $2, ... with the remaining fields, like string.Split(' ')
    set -- ${stat##*") "}

    # The 22nd field is now the 20th one ($# is the number of arguments): skip the 19 fields before it, so that it becomes $1
    [ $# -ge 20 ] || return 1
    shift 19
    start_time=$1
  else
    # macOS has no /proc, but ps can print the full start date of a process ("lstart"), without the column header ("=")
    start_time=$(ps -o lstart= -p "$1")
  fi

  # The last command determines the return value of the function: true if the start time isn't empty
  [ -n "$start_time" ]
}

# The PIDs to kill, as a space-separated string like " 123 456", since POSIX shells have no arrays
pids=

# Reads one line at a time, until the end of the standard input, which happens when the .NET process exits
while read -r line; do
  # Removes the leading "-" if any, so that both "123" and "-123" give "123"
  pid=${line#-}

  # Skips lines that aren't a valid PID: empty, starting with 0, or containing a character that isn't a digit.
  # Starting with 0 matters, as "kill -9 0" would kill every process of the group, including the .NET process.
  case $pid in ''|0*|*[!0-9]*) log "Ignoring invalid line: $line"; continue ;; esac

  # Removes the PID from the list, for both "<pid>" and "-<pid>", so that a PID is never listed twice
  remaining=
  for p in $pids; do [ "$p" = "$pid" ] || remaining="$remaining $p"; done
  pids=$remaining

  if [ "$pid" != "$line" ]; then
    log "Forgetting $pid, it exited"
  elif read_start_time "$pid"; then
    # For "<pid>", adds the PID back to the list, along with its start time
    pids="$pids $pid"

    # POSIX shells have no dictionaries either, so the start time is stored in a variable named after the PID, like start_time_123.
    # "eval" runs a command built from a string, like "start_time_123=$start_time" here.
    eval "start_time_$pid=\$start_time"
    log "Tracking $pid, started at $start_time"
  else
    log "Not tracking $pid, it already exited"
  fi
done

# The .NET process exited: kill the remaining processes, unless their PID now belongs to another process
log "End of standard input, the .NET process exited"

for pid in $pids; do
  eval "expected_start_time=\$start_time_$pid"

  if ! read_start_time "$pid"; then
    log "Not killing $pid, it already exited"
  elif [ "$start_time" != "$expected_start_time" ]; then
    log "Not killing $pid, its PID now belongs to another process started at $start_time"
  else
    # Logged before killing, so that it's already written once the process is observed dead
    log "Killing $pid"

    # "kill -9" sends SIGKILL, which processes can't ignore
    kill -9 "$pid"
  fi
done

log "Watchdog exited"
