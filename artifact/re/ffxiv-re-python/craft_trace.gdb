set debuginfod enabled off
set pagination off
set print thread-events off
set auto-solib-add off
handle SIGUSR1 nostop noprint pass
handle SIGUSR2 nostop noprint pass
handle SIGALRM nostop noprint pass
handle SIGPIPE nostop noprint pass
handle SIGCHLD nostop noprint pass
attach 1157405
break *0x140ea0430
condition 1 ((*(unsigned int *)$r9 == 9 || *(unsigned int *)$r9 == 10) && *(unsigned short *)($rcx + 0x414) >= 38246 && *(unsigned short *)($rcx + 0x414) <= 38253)
commands 1
  silent
  printf "CRAFT_EVENT recipe=%u rlt=%u flags=%u event=%u payload=%p return=%p\n", *(unsigned short *)($rcx + 0x414), *(unsigned short *)($rcx + 0x1f4), *(unsigned short *)($rcx + 0x332), *(unsigned int *)$r9, $r9, *(void **)$rsp
  x/24wx $r9
  bt 12
  detach
  quit
end
continue
