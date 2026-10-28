package com.scrappy.app

import java.net.Inet4Address
import java.net.NetworkInterface

/**
 * The tablet's own addresses, each labelled with the link it lives on.
 *
 * This exists because "wired does not work" and "the tablet is on Wi-Fi" look identical from the
 * PC's side unless the tablet says which addresses it has. When USB tethering is on, `usb0` /
 * `rndis0` carries 192.168.42.x; when it is off, only Wi-Fi is listed - which is the difference
 * between "the cable is not plugged in" and "the cable is plugged in but tethering is off", and
 * the user can only tell by reading this list.
 */
object NetLinks {

    data class Address(val address: String, val link: String)

    /** "Wired", "Wi-Fi" or "Other" - the same words the PC's picker uses. */
    fun linkOf(nic: NetworkInterface): String {
        val haystack = "${nic.name} ${nic.displayName}".lowercase()
        return when {
            haystack.contains("rndis") || haystack.contains("usb") ||
                haystack.contains("tether") -> "Wired"
            haystack.contains("wlan") || haystack.contains("wifi") || haystack.contains("wi-fi") -> "Wi-Fi"
            else -> "Other"
        }
    }

    fun read(port: Int): List<Address> {
        val entries = mutableListOf<Address>()

        try {
            for (nic in java.util.Collections.list(NetworkInterface.getNetworkInterfaces())) {
                if (!nic.isUp || nic.isLoopback) continue

                val link = linkOf(nic)
                for (address in java.util.Collections.list(nic.inetAddresses)) {
                    if (address is Inet4Address && !address.isLoopbackAddress && !address.isLinkLocalAddress) {
                        entries.add(Address("${address.hostAddress}:$port", link))
                    }
                }
            }
        } catch (e: Exception) {
            // A tablet that will not enumerate its interfaces is still a tablet.
        }

        return entries
    }

    /** True when a USB-tethered address exists, i.e. the PC can reach this over the cable. */
    fun hasWired(port: Int): Boolean = read(port).any { it.link == "Wired" }

    fun describe(port: Int): String {
        val entries = read(port)
        if (entries.isEmpty()) return "no network address yet"

        val wired = entries.filter { it.link == "Wired" }
        val wireless = entries.filter { it.link != "Wired" }

        val parts = mutableListOf<String>()
        if (wired.isNotEmpty()) {
            parts.add("USB cable: " + wired.joinToString(", ") { it.address })
        }
        if (wireless.isNotEmpty()) {
            parts.add("Wi-Fi: " + wireless.joinToString(", ") { it.address })
        }

        return parts.joinToString("   ")
    }
}
