package bot

import (
	"net"
	"time"
)

func dial(addr string) (net.Conn, error) {
	c, err := net.DialTimeout("tcp", addr, 3*time.Second)
	if err == nil {
		c.SetReadDeadline(time.Now().Add(5 * time.Second))
	}
	return c, err
}
