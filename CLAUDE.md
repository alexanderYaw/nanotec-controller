# CLAUDE.md

## Commit messages

Never add a `Co-Authored-By:` trailer to commit messages. Commits in this
repository are authored solely by the repository owner.

Why this is a hard rule: on 2026-07-21 a commit carrying
`Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>` was pushed to `main`
and force-pushed away six minutes later. GitHub had already indexed the trailer
and mapped the address to the `claude` account, so `claude` is permanently listed
in the repository's contributors sidebar. The unreachable commit is still served
at `/commit/62fc9db` because GitHub does not garbage-collect it, and the
attribution cannot be removed without a GitHub Support request. A rewrite after
the fact does not undo it — the trailer must never be committed in the first
place.
